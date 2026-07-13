using System.Collections.ObjectModel;
using System.Diagnostics;

namespace LlamaSharp.ToolCallEnvelopes.Internal;

internal sealed class ManagedToolRun
{
    private readonly ILlamaSharpToolExecutor _executor;
    private readonly ToolEnvelopePlan _plan;
    private readonly string _systemPrompt;
    private readonly ToolEnvelopeTurn _firstTurn;
    private readonly ToolDispatcher _dispatch;
    private readonly ToolRunOptions _options;
    private readonly CancellationToken _cancellationToken;
    private readonly List<ToolMessage> _history;
    private readonly List<ToolExecution> _executions = [];
    private readonly Stopwatch _clock = new();
    private ToolChoice _choice;
    private RunPosition _lastPosition;

    internal ManagedToolRun(
        ILlamaSharpToolExecutor executor,
        ToolEnvelopePlan plan,
        string systemPrompt,
        IEnumerable<ToolMessage> history,
        ToolEnvelopeTurn firstTurn,
        ToolDispatcher dispatch,
        ToolRunOptions options,
        CancellationToken cancellationToken)
    {
        _executor = executor;
        _plan = plan;
        _systemPrompt = systemPrompt;
        _history = history.ToList();
        _firstTurn = firstTurn;
        _dispatch = dispatch;
        _options = options;
        _cancellationToken = cancellationToken;
        _choice = options.InitialChoice;
    }

    internal async Task<ToolRunResult> RunAsync()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        _clock.Start();

        for (var turnIndex = 0; turnIndex < _options.MaxModelTurns; turnIndex++)
        {
            var turn = turnIndex == 0
                ? _firstTurn
                : _plan.CreateTurn(_systemPrompt, _history, _choice);
            var turnResult = await GenerateValidOutcomeAsync(turnIndex, turn).ConfigureAwait(false);

            if (turnResult is TurnResult.Failed failed)
                return failed.Result;

            var accepted = (TurnResult.Accepted)turnResult;
            _lastPosition = accepted.Position;

            if (accepted.Outcome is ToolEnvelopeOutcome.Final final)
            {
                AppendFinalMessage(final);
                return Complete(final);
            }

            var request = (ToolEnvelopeOutcome.ToolRequest)accepted.Outcome;
            var dispatchFailure = await DispatchAsync(request, accepted.Position).ConfigureAwait(false);
            if (dispatchFailure is not null)
                return dispatchFailure;

            _choice = _options.FollowUpChoice;
        }

        return Fail(
            ToolRunFailureCode.ModelTurnLimitReached,
            ToolRunFailureMessages.ModelTurnLimit(_options.MaxModelTurns, _lastPosition),
            _lastPosition);
    }

    private async Task<TurnResult> GenerateValidOutcomeAsync(
        int turnIndex,
        ToolEnvelopeTurn firstAttempt)
    {
        var turn = firstAttempt;
        ToolEnvelopeError? lastError = null;
        var lastOutput = string.Empty;

        for (var attemptIndex = 0; attemptIndex < _options.MaxAttemptsPerTurn; attemptIndex++)
        {
            var position = new RunPosition(turnIndex, attemptIndex);
            if (attemptIndex > 0)
            {
                turn = _plan.CreateRepairTurn(
                    _systemPrompt,
                    _history,
                    _choice,
                    lastError!,
                    lastOutput);
            }

            var attempt = await InferAttemptAsync(turn, position).ConfigureAwait(false);
            switch (attempt)
            {
                case AttemptResult.Failed failed:
                    return new TurnResult.Failed(failed.Result);

                case AttemptResult.Rejected rejected:
                    lastError = rejected.Error;
                    lastOutput = rejected.Output;
                    var rejectionObserverFailure = await ObserveAsync(
                        new ToolRunEvent.AttemptRejected(
                            position.TurnIndex,
                            position.AttemptIndex,
                            rejected.Error)).ConfigureAwait(false);
                    if (rejectionObserverFailure is not null)
                        return new TurnResult.Failed(rejectionObserverFailure);
                    break;

                case AttemptResult.Accepted accepted:
                    var acceptanceObserverFailure = await ObserveAsync(
                        new ToolRunEvent.AttemptAccepted(
                            position.TurnIndex,
                            position.AttemptIndex,
                            accepted.Outcome)).ConfigureAwait(false);
                    return acceptanceObserverFailure is null
                        ? new TurnResult.Accepted(accepted.Outcome, position)
                        : new TurnResult.Failed(acceptanceObserverFailure);
            }
        }

        var finalPosition = new RunPosition(turnIndex, _options.MaxAttemptsPerTurn - 1);
        return new TurnResult.Failed(Fail(
            ToolRunFailureCode.InvalidModelOutput,
            ToolRunFailureMessages.InvalidModelOutput(
                finalPosition,
                _options.MaxAttemptsPerTurn,
                lastError!),
            finalPosition,
            envelopeError: lastError));
    }

    private async Task<AttemptResult> InferAttemptAsync(
        ToolEnvelopeTurn turn,
        RunPosition position)
    {
        var startObserverFailure = await ObserveAsync(new ToolRunEvent.AttemptStarted(
            position.TurnIndex,
            position.AttemptIndex,
            turn.Choice)).ConfigureAwait(false);
        if (startObserverFailure is not null)
            return new AttemptResult.Failed(startObserverFailure);

        var reader = turn.CreateStreamReader();
        using var inferenceCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _cancellationToken);
        IAsyncEnumerator<string>? enumerator = null;
        Exception? inferenceException = null;
        ToolRunResult.Failed? observerFailure = null;
        ToolEnvelopeError? earlyError = null;

        try
        {
            var stream = _executor.InferAsync(turn, inferenceCancellation.Token)
                ?? throw new InvalidOperationException(
                    "ILlamaSharpToolExecutor.InferAsync returned null. Return an IAsyncEnumerable "
                    + "that yields only newly generated response fragments, even when it yields no "
                    + "fragments.");
            enumerator = stream.GetAsyncEnumerator(inferenceCancellation.Token)
                ?? throw new InvalidOperationException(
                    "ILlamaSharpToolExecutor.InferAsync returned a stream whose "
                    + "GetAsyncEnumerator method returned null. Return a real async enumerator "
                    + "that yields only newly generated non-null response fragments, or repair "
                    + "the model adapter before retrying this turn.");

            while (await enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                if (enumerator.Current is null)
                {
                    throw new InvalidOperationException(
                        "ILlamaSharpToolExecutor.InferAsync yielded a null fragment. Yield non-null "
                        + "newly generated text fragments; use an empty fragment only when necessary.");
                }

                IReadOnlyList<ToolEnvelopeStreamUpdate> updates;
                try
                {
                    updates = reader.Feed(enumerator.Current);
                }
                catch (ToolEnvelopeException exception)
                {
                    earlyError = exception.Error;
                    await inferenceCancellation.CancelAsync().ConfigureAwait(false);
                    break;
                }

                foreach (var update in updates)
                {
                    observerFailure = await ObserveAsync(MapUpdate(update, position))
                        .ConfigureAwait(false);
                    if (observerFailure is not null)
                    {
                        await inferenceCancellation.CancelAsync().ConfigureAwait(false);
                        break;
                    }
                }

                if (observerFailure is not null)
                    break;
            }
        }
        catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException) when (inferenceCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            inferenceException = exception;
        }

        if (enumerator is not null)
        {
            try
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                inferenceCancellation.IsCancellationRequested
                && !_cancellationToken.IsCancellationRequested)
            {
            }
            catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                inferenceException ??= exception;
            }
        }

        _cancellationToken.ThrowIfCancellationRequested();

        if (observerFailure is not null)
            return new AttemptResult.Failed(observerFailure);
        if (inferenceException is not null)
        {
            return new AttemptResult.Failed(Fail(
                ToolRunFailureCode.InferenceFailed,
                ToolRunFailureMessages.Inference(
                    position,
                    inferenceException,
                    ExceptionDetail(inferenceException)),
                position,
                exception: inferenceException));
        }
        if (earlyError is not null)
            return new AttemptResult.Rejected(earlyError, reader.RawOutput);

        return reader.TryComplete(out var outcome, out var error)
            ? new AttemptResult.Accepted(outcome!)
            : new AttemptResult.Rejected(error!, reader.RawOutput);
    }

    private async Task<ToolRunResult.Failed?> DispatchAsync(
        ToolEnvelopeOutcome.ToolRequest request,
        RunPosition position)
    {
        _history.Add(ToolMessage.AssistantCalls(request.Calls));

        foreach (var call in request.Calls)
        {
            var startObserverFailure = await ObserveAsync(new ToolRunEvent.ToolDispatchStarted(
                position.TurnIndex,
                position.AttemptIndex,
                call)).ConfigureAwait(false);
            if (startObserverFailure is not null)
                return startObserverFailure;

            string? result;
            try
            {
                result = await _dispatch(call, _cancellationToken).ConfigureAwait(false);
                _cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return Fail(
                    ToolRunFailureCode.ToolExecutionFailed,
                    ToolRunFailureMessages.ToolExecution(
                        position,
                        call,
                        exception,
                        ExceptionDetail(exception)),
                    position,
                    exception: exception);
            }

            if (result is null)
            {
                return Fail(
                    ToolRunFailureCode.ToolResultWasNull,
                    ToolRunFailureMessages.NullToolResult(position, call),
                    position);
            }

            var resultLimit = _plan.Options.Limits.MaxToolResultCharacters;
            if (result.Length > resultLimit)
            {
                return Fail(
                    ToolRunFailureCode.ToolResultTooLarge,
                    ToolRunFailureMessages.ToolResultTooLarge(
                        position,
                        call,
                        result.Length,
                        resultLimit),
                    position);
            }

            var execution = new ToolExecution(position.TurnIndex, call, result);
            _executions.Add(execution);
            _history.Add(ToolMessage.ToolResult(call, result));

            var completionObserverFailure = await ObserveAsync(
                new ToolRunEvent.ToolDispatchCompleted(
                    position.TurnIndex,
                    position.AttemptIndex,
                    execution)).ConfigureAwait(false);
            if (completionObserverFailure is not null)
                return completionObserverFailure;
        }

        return null;
    }

    private async ValueTask<ToolRunResult.Failed?> ObserveAsync(ToolRunEvent update)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (_options.Observer is null)
            return null;

        try
        {
            await _options.Observer(update, _cancellationToken).ConfigureAwait(false);
            _cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
        catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var position = new RunPosition(update.TurnIndex, update.AttemptIndex);
            return Fail(
                ToolRunFailureCode.ObserverFailed,
                ToolRunFailureMessages.Observer(
                    position,
                    update,
                    exception,
                    ExceptionDetail(exception)),
                position,
                exception: exception);
        }
    }

    private ToolRunResult.Completed Complete(ToolEnvelopeOutcome.Final outcome)
    {
        _clock.Stop();
        return new ToolRunResult.Completed(
            outcome,
            Snapshot(_executions),
            Snapshot(_history),
            _clock.Elapsed);
    }

    private void AppendFinalMessage(ToolEnvelopeOutcome.Final outcome)
    {
        switch (outcome)
        {
            case ToolEnvelopeOutcome.AssistantMessage message:
                _history.Add(ToolMessage.Assistant(message.Text));
                break;
            case ToolEnvelopeOutcome.Refusal refusal:
                _history.Add(ToolMessage.AssistantRefusal(refusal.Reason));
                break;
            default:
                throw new InvalidOperationException(
                    $"The parser returned unsupported final outcome type "
                    + $"'{outcome.GetType().FullName}'. This indicates a package defect because "
                    + "every final outcome must have an explicit conversation representation.");
        }
    }

    private ToolRunResult.Failed Fail(
        ToolRunFailureCode code,
        string message,
        RunPosition position,
        ToolEnvelopeError? envelopeError = null,
        Exception? exception = null)
    {
        _clock.Stop();
        var failure = new ToolRunFailure(
            code,
            message,
            position.TurnIndex,
            position.AttemptIndex,
            envelopeError,
            exception);
        return new ToolRunResult.Failed(
            failure,
            Snapshot(_executions),
            Snapshot(_history),
            _clock.Elapsed);
    }

    private static ToolRunEvent MapUpdate(
        ToolEnvelopeStreamUpdate update,
        RunPosition position) => update switch
        {
            ToolEnvelopeStreamUpdate.AssistantTextDelta text =>
                new ToolRunEvent.AssistantTextDelta(
                    position.TurnIndex,
                    position.AttemptIndex,
                    text.Text),
            ToolEnvelopeStreamUpdate.RefusalDelta refusal =>
                new ToolRunEvent.RefusalDelta(
                    position.TurnIndex,
                    position.AttemptIndex,
                    refusal.Text),
            ToolEnvelopeStreamUpdate.ToolArgumentsDelta arguments =>
                new ToolRunEvent.ToolArgumentsDelta(
                    position.TurnIndex,
                    position.AttemptIndex,
                    arguments.CallIndex,
                    arguments.Json),
            _ => throw new InvalidOperationException(
                $"The stream reader returned unsupported update type '{update.GetType().FullName}'. "
                + "This indicates a package defect because every public stream update must have a "
                + "managed-run event mapping."),
        };

    private static ReadOnlyCollection<T> Snapshot<T>(IEnumerable<T> values) =>
        Array.AsReadOnly(values.ToArray());

    private string ExceptionDetail(Exception exception) =>
        _plan.CreateDiagnosticPreview(
            string.IsNullOrWhiteSpace(exception.Message)
                ? "The exception supplied no message."
                : exception.Message);

    private abstract record AttemptResult
    {
        private AttemptResult()
        {
        }

        internal sealed record Accepted(ToolEnvelopeOutcome Outcome) : AttemptResult;
        internal sealed record Rejected(ToolEnvelopeError Error, string Output) : AttemptResult;
        internal sealed record Failed(ToolRunResult.Failed Result) : AttemptResult;
    }

    private abstract record TurnResult
    {
        private TurnResult()
        {
        }

        internal sealed record Accepted(
            ToolEnvelopeOutcome Outcome,
            RunPosition Position) : TurnResult;

        internal sealed record Failed(ToolRunResult.Failed Result) : TurnResult;
    }

}

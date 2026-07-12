using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Diagnostics;

namespace LlamaSharp.ToolCallEnvelopes;

/// <summary>
/// Adapter boundary for a LlamaSharp host. The host renders
/// <see cref="ToolPromptHistory"/> through its chosen chat template and feeds
/// the returned grammar text into its LlamaSharp sampling pipeline.
/// </summary>
public interface ILlamaSharpToolExecutor
{
    IAsyncEnumerable<string> InferAsync(
        ToolPromptHistory prompt,
        string grammar,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Configuration for the managed tool-call loop.
/// </summary>
public sealed record LlamaSharpToolRunOptions
{
    public ToolChoice ToolChoice { get; init; } = ToolChoice.Auto;
    public ToolEnvelopeMode EnvelopeMode { get; init; } = ToolEnvelopeMode.Inferred;
    public ToolEnvelopeStreamValidation StreamValidation { get; init; } = ToolEnvelopeStreamValidation.Strict;
    public bool ParallelToolCalls { get; init; } = true;
    public bool StrictTools { get; init; } = true;
    public bool AllowRefusal { get; init; }
    public bool RepairInvalidEnvelope { get; init; }
    public int MaxRepairAttempts { get; init; }
    public int MaxTurns { get; init; } = 4;
    public string? SystemPrompt { get; init; }
    public bool AllowLegacyCalls { get; init; } = true;
}

/// <summary>
/// Quality and control-flow metadata for one managed run.
/// </summary>
public sealed record LlamaSharpToolRunMetadata(
    ToolEnvelopeResultMode FinalMode,
    int RepairCount,
    IReadOnlyList<ToolCall> ToolCalls,
    TimeSpan Elapsed,
    string? LastInvalidModelOutput);

/// <summary>
/// Successful managed-run value.
/// </summary>
public sealed record LlamaSharpToolRunValue(
    ToolEnvelopeResult Envelope,
    LlamaSharpToolRunMetadata Metadata)
{
    public string? AssistantText => Envelope.Content;
    public string? Refusal => Envelope.Refusal;
    public IReadOnlyList<ToolCall> ToolCalls => Envelope.ToolCalls;
}

/// <summary>
/// A non-throwing managed-run failure.
/// </summary>
public sealed record LlamaSharpToolRunError(
    string Code,
    string Message,
    string JsonPath,
    int RepairCount,
    string? LastRawModelOutput,
    Exception? Exception = null);

/// <summary>
/// Result returned by <see cref="LlamaSharpToolCallRunner.TryRunAsync"/>.
/// </summary>
public sealed record LlamaSharpToolRunResult(
    bool Success,
    LlamaSharpToolRunValue? Value,
    LlamaSharpToolRunError? Error)
{
    public static LlamaSharpToolRunResult FromValue(LlamaSharpToolRunValue value) =>
        new(true, value, null);

    public static LlamaSharpToolRunResult FromError(LlamaSharpToolRunError error) =>
        new(false, null, error);
}

/// <summary>
/// Thrown by the throwing managed-run entry point after all configured repair
/// attempts are exhausted or a tool execution fails.
/// </summary>
public sealed class LlamaSharpToolRunException : Exception
{
    public LlamaSharpToolRunError Error { get; }

    public LlamaSharpToolRunException(LlamaSharpToolRunError error)
        : base(error.Message, error.Exception)
    {
        Error = error;
    }
}

/// <summary>
/// Base type for managed streaming updates.
/// </summary>
public abstract record ToolRunUpdate;

public sealed record AssistantTextDelta(string Text) : ToolRunUpdate;

public sealed record ToolCallArgumentsDelta(int Index, string Fragment) : ToolRunUpdate;

public sealed record ToolCallStarted(ToolCall Call) : ToolRunUpdate;

public sealed record ToolCallCompleted(ToolCall Call) : ToolRunUpdate;

public sealed record ToolEnvelopeRepairStarted(LlamaSharpToolEnvelopeException Error) : ToolRunUpdate;

public sealed record ToolRunCompleted(LlamaSharpToolRunValue Result) : ToolRunUpdate;

public sealed record ToolRunFailed(LlamaSharpToolRunError Error) : ToolRunUpdate;

/// <summary>
/// Manages the repeated prompt, grammar, inference, parse, dispatch, and
/// tool-result-history turns around the package's low-level APIs.
/// </summary>
public static class LlamaSharpToolCallRunner
{
    public static async Task<LlamaSharpToolRunValue> RunAsync(
        ILlamaSharpToolExecutor executor,
        IReadOnlyList<ToolAwareMessage> conversation,
        IReadOnlyList<ToolDefinition> tools,
        Func<ToolCall, CancellationToken, Task<string>> dispatchToolAsync,
        LlamaSharpToolRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(dispatchToolAsync);

        await foreach (var update in RunStreamingAsync(
                           executor,
                           conversation,
                           tools,
                           dispatchToolAsync,
                           options,
                           cancellationToken))
        {
            if (update is ToolRunCompleted completed)
                return completed.Result;

            if (update is ToolRunFailed failed)
                throw new LlamaSharpToolRunException(failed.Error);
        }

        throw new LlamaSharpToolRunException(new LlamaSharpToolRunError(
            "RunIncomplete",
            "The managed tool run ended without a final message or refusal.",
            "$",
            0,
            null));
    }

    public static async Task<LlamaSharpToolRunResult> TryRunAsync(
        ILlamaSharpToolExecutor executor,
        IReadOnlyList<ToolAwareMessage> conversation,
        IReadOnlyList<ToolDefinition> tools,
        Func<ToolCall, CancellationToken, Task<string>> dispatchToolAsync,
        LlamaSharpToolRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return LlamaSharpToolRunResult.FromValue(await RunAsync(
                executor,
                conversation,
                tools,
                dispatchToolAsync,
                options,
                cancellationToken));
        }
        catch (LlamaSharpToolRunException ex)
        {
            return LlamaSharpToolRunResult.FromError(ex.Error);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            return LlamaSharpToolRunResult.FromError(new LlamaSharpToolRunError(
                "ModelCancelled",
                "The managed tool run was cancelled.",
                "$",
                0,
                null,
                ex));
        }
    }

    public static async IAsyncEnumerable<ToolRunUpdate> RunStreamingAsync(
        ILlamaSharpToolExecutor executor,
        IReadOnlyList<ToolAwareMessage> conversation,
        IReadOnlyList<ToolDefinition> tools,
        Func<ToolCall, CancellationToken, Task<string>> dispatchToolAsync,
        LlamaSharpToolRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(dispatchToolAsync);
        options ??= new LlamaSharpToolRunOptions();
        ArgumentNullException.ThrowIfNull(options.ToolChoice);

        var started = Stopwatch.StartNew();
        var messages = new List<ToolAwareMessage>(conversation);
        var validation = ToolDefinitionValidator.Validate(
            tools,
            new ToolDefinitionValidationOptions
            {
                RejectUnsupportedJsonSchemaKeywords = options.StrictTools,
            });

        if (!validation.Success)
        {
            yield return new ToolRunFailed(new LlamaSharpToolRunError(
                "InvalidToolDefinitions",
                string.Join("; ", validation.Errors.Select(error =>
                    $"{error.ToolName} {error.JsonPath}: {error.Message}")),
                validation.Errors[0].JsonPath,
                0,
                null));
            yield break;
        }

        var repairCount = 0;
        string? lastInvalidOutput = null;
        var allExecutedCalls = new List<ToolCall>();
        var forcedToolChoiceSatisfied = false;

        for (var turn = 0; turn < Math.Max(1, options.MaxTurns); turn++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var turnToolChoice = forcedToolChoiceSatisfied
                                 && (options.ToolChoice.Mode is ToolChoiceMode.Required or ToolChoiceMode.Named)
                ? ToolChoice.Auto
                : options.ToolChoice;

            ToolPromptHistory? prompt = null;
            string? grammar = null;
            LlamaSharpToolRunError? preparationError = null;
            try
            {
                prompt = LlamaSharpToolPromptBuilder.Build(
                    options.SystemPrompt,
                    messages,
                    tools,
                    new ToolPromptOptions
                    {
                        ToolChoice = turnToolChoice,
                        EnvelopeMode = options.EnvelopeMode,
                        StrictTools = options.StrictTools,
                        AllowRefusal = options.AllowRefusal,
                    });
                grammar = LlamaSharpToolGrammar.BuildCompleteEnvelopeGrammar(
                    tools,
                    new ToolEnvelopeGrammarOptions
                    {
                        ToolChoice = turnToolChoice,
                        EnvelopeMode = options.EnvelopeMode,
                        ParallelToolCalls = options.ParallelToolCalls,
                        StrictTools = options.StrictTools,
                        AllowRefusal = options.AllowRefusal,
                    });
            }
            catch (Exception ex) when (ex is ArgumentException or LlamaSharpToolSchemaException)
            {
                preparationError = new LlamaSharpToolRunError(
                    "PreparationFailed",
                    ex.Message,
                    "$",
                    repairCount,
                    lastInvalidOutput,
                    ex);
            }

            if (preparationError is not null)
            {
                yield return new ToolRunFailed(preparationError);
                yield break;
            }

            var parseOptions = new ToolEnvelopeParserOptions
            {
                EnvelopeMode = options.EnvelopeMode,
                AllowLegacyCalls = options.AllowLegacyCalls,
            };
            var streamParser = new LlamaSharpToolEnvelopeStreamParser(
                parseOptions,
                options.StreamValidation);
            var raw = new StringBuilder();
            var partialCalls = new Dictionary<int, PartialCall>();
            LlamaSharpToolEnvelopeException? streamError = null;
            Exception? inferenceError = null;

            using var turnCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var enumerator = executor.InferAsync(prompt!, grammar!, turnCancellation.Token)
                .GetAsyncEnumerator(turnCancellation.Token);
            try
            {
                while (true)
                {
                    bool hasNext;
                    try
                    {
                        hasNext = await enumerator.MoveNextAsync();
                    }
                    catch (Exception ex)
                    {
                        inferenceError = ex;
                        break;
                    }

                    if (!hasNext)
                        break;

                    var token = enumerator.Current;
                    raw.Append(token);
                    var feedResult = TryFeed(streamParser, token);
                    if (feedResult.Error is not null)
                    {
                        streamError = feedResult.Error;
                        turnCancellation.Cancel();
                        break;
                    }

                    foreach (var chunk in feedResult.Chunks)
                    {
                        if (chunk.TextDelta is { } text)
                        {
                            yield return new AssistantTextDelta(text);
                        }
                        else if (chunk.ToolCallDelta is { } delta)
                        {
                            if (!partialCalls.TryGetValue(delta.Index, out var partial))
                            {
                                partial = new PartialCall();
                                partialCalls.Add(delta.Index, partial);
                            }

                            if (delta.Id is { } id)
                                partial.Id = id;
                            if (delta.Name is { } name)
                                partial.Name = name;
                            if (delta.ArgumentsFragment is { } fragment)
                            {
                                partial.Arguments.Append(fragment);
                                yield return new ToolCallArgumentsDelta(delta.Index, fragment);
                            }

                            if (!partial.Started
                                && partial.Id is not null
                                && partial.Name is not null)
                            {
                                partial.Started = true;
                                yield return new ToolCallStarted(new ToolCall(
                                    partial.Id,
                                    partial.Name,
                                    partial.Arguments.ToString()));
                            }
                        }
                    }
                }
            }
            finally
            {
                await enumerator.DisposeAsync();
            }

            if (inferenceError is not null)
            {
                if (inferenceError is OperationCanceledException
                    && cancellationToken.IsCancellationRequested)
                {
                    yield return new ToolRunFailed(new LlamaSharpToolRunError(
                        "ModelCancelled",
                        "The managed tool run was cancelled.",
                        "$",
                        repairCount,
                        raw.ToString(),
                        inferenceError));
                    yield break;
                }

                yield return new ToolRunFailed(new LlamaSharpToolRunError(
                    "InferenceFailed",
                    inferenceError.Message,
                    "$",
                    repairCount,
                    raw.ToString(),
                    inferenceError));
                yield break;
            }

            if (streamError is not null)
            {
                lastInvalidOutput = raw.ToString();
                if (CanRepair(options, repairCount))
                {
                    repairCount++;
                    yield return new ToolEnvelopeRepairStarted(streamError);
                    messages.Add(ToolAwareMessage.User(BuildRepairPrompt(streamError)));
                    continue;
                }

                yield return new ToolRunFailed(ToRunError(streamError, repairCount, lastInvalidOutput));
                yield break;
            }

            ToolEnvelopeResult? result = null;
            LlamaSharpToolEnvelopeException? parseError = null;
            try
            {
                result = streamParser.Complete();
            }
            catch (LlamaSharpToolEnvelopeException ex)
            {
                parseError = ex;
            }

            if (parseError is not null)
            {
                lastInvalidOutput = raw.ToString();
                if (CanRepair(options, repairCount))
                {
                    repairCount++;
                    yield return new ToolEnvelopeRepairStarted(parseError);
                    messages.Add(ToolAwareMessage.User(BuildRepairPrompt(parseError)));
                    continue;
                }

                yield return new ToolRunFailed(ToRunError(parseError, repairCount, lastInvalidOutput));
                yield break;
            }

            if (result!.Kind == ToolEnvelopeResultMode.Message
                || result.Kind == ToolEnvelopeResultMode.Refusal)
            {
                var value = new LlamaSharpToolRunValue(
                    result,
                    new LlamaSharpToolRunMetadata(
                    result.Kind,
                        repairCount,
                        allExecutedCalls,
                        started.Elapsed,
                        lastInvalidOutput));
                yield return new ToolRunCompleted(value);
                yield break;
            }

            messages.Add(ToolAwareMessage.AssistantWithToolCalls(result.ToolCalls, result.Content));
            for (var callIndex = 0; callIndex < result.ToolCalls.Count; callIndex++)
            {
                var call = result.ToolCalls[callIndex];
                yield return new ToolCallCompleted(call);
                string? toolResult = null;
                Exception? toolError = null;
                try
                {
                    toolResult = await dispatchToolAsync(call, cancellationToken);
                }
                catch (Exception ex)
                {
                    toolError = ex;
                }

                if (toolError is not null)
                {
                    yield return new ToolRunFailed(new LlamaSharpToolRunError(
                        "ToolExecutionFailed",
                        toolError.Message,
                        $"$.calls[{callIndex}]",
                        repairCount,
                        raw.ToString(),
                        toolError));
                    yield break;
                }

                allExecutedCalls.Add(call);
                messages.Add(ToolAwareMessage.ToolResult(call.Id, toolResult!));
            }
            forcedToolChoiceSatisfied = true;
        }

        yield return new ToolRunFailed(new LlamaSharpToolRunError(
            "MaxTurnsExceeded",
            "The model did not produce a final answer within the configured turn limit.",
            "$",
            repairCount,
            lastInvalidOutput));
    }

    private static bool CanRepair(LlamaSharpToolRunOptions options, int repairCount) =>
        options.RepairInvalidEnvelope && repairCount < Math.Max(0, options.MaxRepairAttempts);

    private static string BuildRepairPrompt(LlamaSharpToolEnvelopeException error) =>
        "Your previous response was not a valid tool envelope. Return one corrected " +
        $"envelope only. Error code: {error.Code}. JSON path: {error.JsonPath}. " +
        $"Problem: {error.Message}";

    private static LlamaSharpToolRunError ToRunError(
        LlamaSharpToolEnvelopeException error,
        int repairCount,
        string rawOutput) =>
        repairCount > 0
            ? new(
                "RepairExhausted",
                $"The envelope remained invalid after {repairCount} repair attempt(s): {error.Message}",
                error.JsonPath,
                repairCount,
                rawOutput,
                error)
            : new(error.Code, error.Message, error.JsonPath, repairCount, rawOutput, error);

    private static FeedResult TryFeed(
        LlamaSharpToolEnvelopeStreamParser parser,
        string token)
    {
        try
        {
            return new FeedResult(parser.Feed(token), null);
        }
        catch (LlamaSharpToolEnvelopeException ex)
        {
            return new FeedResult([], ex);
        }
    }

    private sealed record FeedResult(
        IReadOnlyList<ToolEnvelopeStreamChunk> Chunks,
        LlamaSharpToolEnvelopeException? Error);

    private sealed class PartialCall
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public StringBuilder Arguments { get; } = new();
        public bool Started { get; set; }
    }
}

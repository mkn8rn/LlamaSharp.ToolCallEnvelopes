namespace LlamaSharp.ToolCallEnvelopes;

/// <summary>Identifies a managed-run failure.</summary>
public enum ToolRunFailureCode
{
    /// <summary>The model executor failed while creating, enumerating, or disposing its stream.</summary>
    InferenceFailed,
    /// <summary>All bounded generation attempts produced invalid model envelopes.</summary>
    InvalidModelOutput,
    /// <summary>The host dispatcher threw while executing a validated call.</summary>
    ToolExecutionFailed,
    /// <summary>The host dispatcher returned a null result.</summary>
    ToolResultWasNull,
    /// <summary>The host dispatcher returned more result text than the plan permits.</summary>
    ToolResultTooLarge,
    /// <summary>The run used every accepted model turn without reaching a final outcome.</summary>
    ModelTurnLimitReached,
    /// <summary>The optional observer threw while handling an ordered run event.</summary>
    ObserverFailed,
}

/// <summary>One completed and sequentially dispatched tool call.</summary>
public sealed class ToolExecution
{
    internal ToolExecution(int turnIndex, ToolCall call, string result)
    {
        TurnIndex = turnIndex;
        Call = call;
        Result = result;
    }

    /// <summary>The zero-based model turn that requested the call.</summary>
    public int TurnIndex { get; }
    /// <summary>The validated call executed by the dispatcher.</summary>
    public ToolCall Call { get; }
    /// <summary>The non-null, bounded dispatcher result added to history.</summary>
    public string Result { get; }
}

/// <summary>A typed managed-run failure with bounded model diagnostics.</summary>
public sealed class ToolRunFailure
{
    internal ToolRunFailure(
        ToolRunFailureCode code,
        string message,
        int turnIndex,
        int attemptIndex,
        ToolEnvelopeError? envelopeError = null,
        Exception? exception = null)
    {
        Code = code;
        Message = message;
        TurnIndex = turnIndex;
        AttemptIndex = attemptIndex;
        EnvelopeError = envelopeError;
        Exception = exception;
    }

    /// <summary>The typed failure category.</summary>
    public ToolRunFailureCode Code { get; }
    /// <summary>A descriptive failure location, observed state, and recovery action.</summary>
    public string Message { get; }
    /// <summary>The zero-based model turn where the run stopped.</summary>
    public int TurnIndex { get; }
    /// <summary>The zero-based generation attempt where the run stopped.</summary>
    public int AttemptIndex { get; }
    /// <summary>The last model-envelope error, when model output caused the failure.</summary>
    public ToolEnvelopeError? EnvelopeError { get; }
    /// <summary>The original host exception, when a callback or executor caused the failure.</summary>
    public Exception? Exception { get; }
}

/// <summary>A closed completed-or-failed result from the managed runner.</summary>
public abstract class ToolRunResult
{
    private ToolRunResult(
        IReadOnlyList<ToolExecution> executions,
        IReadOnlyList<ToolMessage> conversation,
        TimeSpan elapsed)
    {
        Executions = executions;
        Conversation = conversation;
        Elapsed = elapsed;
    }

    /// <summary>All successful dispatches in execution order.</summary>
    public IReadOnlyList<ToolExecution> Executions { get; }

    /// <summary>The original and package-appended conversation snapshot.</summary>
    public IReadOnlyList<ToolMessage> Conversation { get; }

    /// <summary>Total managed-run elapsed time.</summary>
    public TimeSpan Elapsed { get; }

    /// <summary>A final answer or refusal.</summary>
    public sealed class Completed : ToolRunResult
    {
        internal Completed(
            ToolEnvelopeOutcome.Final outcome,
            IReadOnlyList<ToolExecution> executions,
            IReadOnlyList<ToolMessage> conversation,
            TimeSpan elapsed)
            : base(executions, conversation, elapsed) => Outcome = outcome;

        /// <summary>The final validated answer or refusal.</summary>
        public ToolEnvelopeOutcome.Final Outcome { get; }
    }

    /// <summary>A non-cancellation failure.</summary>
    public sealed class Failed : ToolRunResult
    {
        internal Failed(
            ToolRunFailure failure,
            IReadOnlyList<ToolExecution> executions,
            IReadOnlyList<ToolMessage> conversation,
            TimeSpan elapsed)
            : base(executions, conversation, elapsed) => Failure = failure;

        /// <summary>The typed non-cancellation failure.</summary>
        public ToolRunFailure Failure { get; }
    }
}

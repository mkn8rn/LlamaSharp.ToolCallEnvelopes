namespace LlamaSharp.ToolCallEnvelopes;

/// <summary>One ordered, non-terminal managed-run event.</summary>
public abstract class ToolRunEvent
{
    private ToolRunEvent(int turnIndex, int attemptIndex)
    {
        TurnIndex = turnIndex;
        AttemptIndex = attemptIndex;
    }

    /// <summary>The zero-based accepted-model-turn position.</summary>
    public int TurnIndex { get; }
    /// <summary>The zero-based generation-attempt position within the turn.</summary>
    public int AttemptIndex { get; }

    /// <summary>Signals that one model generation attempt is about to start.</summary>
    public sealed class AttemptStarted : ToolRunEvent
    {
        internal AttemptStarted(int turnIndex, int attemptIndex, ToolChoice choice)
            : base(turnIndex, attemptIndex) => Choice = choice;

        /// <summary>The tool-choice policy compiled into this attempt.</summary>
        public ToolChoice Choice { get; }
    }

    /// <summary>Publishes a provisional decoded assistant-text fragment.</summary>
    public sealed class AssistantTextDelta : ToolRunEvent
    {
        internal AssistantTextDelta(int turnIndex, int attemptIndex, string text)
            : base(turnIndex, attemptIndex) => Text = text;

        /// <summary>The provisional decoded fragment.</summary>
        public string Text { get; }
    }

    /// <summary>Publishes a provisional decoded refusal fragment.</summary>
    public sealed class RefusalDelta : ToolRunEvent
    {
        internal RefusalDelta(int turnIndex, int attemptIndex, string text)
            : base(turnIndex, attemptIndex) => Text = text;

        /// <summary>The provisional decoded fragment.</summary>
        public string Text { get; }
    }

    /// <summary>Publishes provisional raw JSON for one tool arguments object.</summary>
    public sealed class ToolArgumentsDelta : ToolRunEvent
    {
        internal ToolArgumentsDelta(int turnIndex, int attemptIndex, int callIndex, string json)
            : base(turnIndex, attemptIndex)
        {
            CallIndex = callIndex;
            Json = json;
        }

        /// <summary>The zero-based call position receiving this fragment.</summary>
        public int CallIndex { get; }
        /// <summary>The provisional raw JSON fragment.</summary>
        public string Json { get; }
    }

    /// <summary>Commits one parsed and validated model outcome.</summary>
    public sealed class AttemptAccepted : ToolRunEvent
    {
        internal AttemptAccepted(int turnIndex, int attemptIndex, ToolEnvelopeOutcome outcome)
            : base(turnIndex, attemptIndex) => Outcome = outcome;

        /// <summary>The authoritative outcome accepted for this turn.</summary>
        public ToolEnvelopeOutcome Outcome { get; }
    }

    /// <summary>Reports one rejected model response before a possible repair attempt.</summary>
    public sealed class AttemptRejected : ToolRunEvent
    {
        internal AttemptRejected(int turnIndex, int attemptIndex, ToolEnvelopeError error)
            : base(turnIndex, attemptIndex) => Error = error;

        /// <summary>The authoritative validation error for the rejected response.</summary>
        public ToolEnvelopeError Error { get; }
    }

    /// <summary>Signals that sequential dispatch of one validated call is about to start.</summary>
    public sealed class ToolDispatchStarted : ToolRunEvent
    {
        internal ToolDispatchStarted(int turnIndex, int attemptIndex, ToolCall call)
            : base(turnIndex, attemptIndex) => Call = call;

        /// <summary>The validated call about to be dispatched.</summary>
        public ToolCall Call { get; }
    }

    /// <summary>Commits one successful dispatch and its bounded result.</summary>
    public sealed class ToolDispatchCompleted : ToolRunEvent
    {
        internal ToolDispatchCompleted(int turnIndex, int attemptIndex, ToolExecution execution)
            : base(turnIndex, attemptIndex) => Execution = execution;

        /// <summary>The committed call execution.</summary>
        public ToolExecution Execution { get; }
    }
}

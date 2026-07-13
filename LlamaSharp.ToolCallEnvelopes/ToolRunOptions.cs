namespace LlamaSharp.ToolCallEnvelopes;

/// <summary>Control-flow options for the managed runner.</summary>
public sealed record ToolRunOptions
{
    /// <summary>The choice policy before the first successful tool batch.</summary>
    public ToolChoice InitialChoice { get; init; } = ToolChoice.Auto;

    /// <summary>Auto or None after the first successful tool batch.</summary>
    public ToolChoice FollowUpChoice { get; init; } = ToolChoice.Auto;

    /// <summary>Maximum accepted model turns, including tool-request turns.</summary>
    public int MaxModelTurns { get; init; } = 4;

    /// <summary>Maximum generation attempts for each model turn.</summary>
    public int MaxAttemptsPerTurn { get; init; } = 2;

    /// <summary>An optional ordered observer for provisional and committed events.</summary>
    public ToolRunObserver? Observer { get; init; }

    internal void Validate()
    {
        if (InitialChoice is null)
        {
            throw new ArgumentNullException(
                nameof(InitialChoice),
                "ToolRunOptions.InitialChoice is required. Use ToolChoice.Auto for the "
                + "usual model-directed flow, or explicitly select None, Required, or Named(name).");
        }

        if (FollowUpChoice is null)
        {
            throw new ArgumentNullException(
                nameof(FollowUpChoice),
                "ToolRunOptions.FollowUpChoice is required. Use ToolChoice.Auto to allow another "
                + "tool request after a result, or ToolChoice.None to require a final response.");
        }

        if (FollowUpChoice.Kind is not (ToolChoiceKind.Auto or ToolChoiceKind.None))
        {
            throw new ArgumentException(
                $"ToolRunOptions.FollowUpChoice is {FollowUpChoice}, but managed follow-up turns "
                + "accept only ToolChoice.Auto or ToolChoice.None. Use Auto when the model may need "
                + "another tool, or None when the next response must be final. Requiring or naming "
                + "a tool after every result can force an accidental tool loop.",
                nameof(FollowUpChoice));
        }

        if (MaxModelTurns <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxModelTurns),
                MaxModelTurns,
                "ToolRunOptions.MaxModelTurns must be greater than zero because every managed run "
                + "needs capacity for at least one model response.");
        }

        if (MaxAttemptsPerTurn <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxAttemptsPerTurn),
                MaxAttemptsPerTurn,
                "ToolRunOptions.MaxAttemptsPerTurn must be greater than zero because each model "
                + "turn needs at least one generation attempt.");
        }
    }
}

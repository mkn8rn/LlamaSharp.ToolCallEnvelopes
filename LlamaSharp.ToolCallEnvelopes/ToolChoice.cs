namespace LlamaSharp.ToolCallEnvelopes;

/// <summary>
/// Declares how the model should select tools for an envelope-constrained turn.
/// </summary>
public enum ToolChoiceMode
{
    /// <summary>The model may answer directly or call tools.</summary>
    Auto = 0,

    /// <summary>The model must not call a tool.</summary>
    None,

    /// <summary>The model must emit at least one tool call.</summary>
    Required,

    /// <summary>The model must call the named function.</summary>
    Named,
}

/// <summary>
/// Tool-selection policy used when building the envelope grammar.
/// </summary>
public sealed record ToolChoice(
    ToolChoiceMode Mode,
    string? NamedFunction = null)
{
    public static ToolChoice Auto { get; } = new(ToolChoiceMode.Auto);
    public static ToolChoice None { get; } = new(ToolChoiceMode.None);
    public static ToolChoice Required { get; } = new(ToolChoiceMode.Required);

    public static ToolChoice ForFunction(string name) =>
        string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("A named tool choice requires a function name.", nameof(name))
            : new ToolChoice(ToolChoiceMode.Named, name);
}

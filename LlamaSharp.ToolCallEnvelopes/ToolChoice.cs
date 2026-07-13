namespace LlamaSharp.ToolCallEnvelopes;

/// <summary>Describes what the model may return for one turn.</summary>
public enum ToolChoiceKind
{
    /// <summary>The model may answer directly or request an available tool.</summary>
    Auto,

    /// <summary>The model must answer directly and cannot request a tool.</summary>
    None,

    /// <summary>The model must request at least one available tool.</summary>
    Required,

    /// <summary>The model must request one specific tool.</summary>
    Named,
}

/// <summary>
/// A valid-by-construction tool-selection policy for one model turn.
/// </summary>
public sealed class ToolChoice : IEquatable<ToolChoice>
{
    private ToolChoice(ToolChoiceKind kind, string? toolName)
    {
        Kind = kind;
        ToolName = toolName;
    }

    /// <summary>Allows a direct answer or an available tool request.</summary>
    public static ToolChoice Auto { get; } = new(ToolChoiceKind.Auto, null);

    /// <summary>Allows a direct answer only.</summary>
    public static ToolChoice None { get; } = new(ToolChoiceKind.None, null);

    /// <summary>Requires at least one available tool request.</summary>
    public static ToolChoice Required { get; } = new(ToolChoiceKind.Required, null);

    /// <summary>The policy kind.</summary>
    public ToolChoiceKind Kind { get; }

    /// <summary>The required tool name for <see cref="ToolChoiceKind.Named"/>.</summary>
    public string? ToolName { get; }

    /// <summary>Requires the model to request <paramref name="toolName"/>.</summary>
    public static ToolChoice Named(string toolName)
    {
        ToolDefinition.ValidateName(toolName, nameof(toolName));
        return new ToolChoice(ToolChoiceKind.Named, toolName);
    }

    /// <inheritdoc />
    public bool Equals(ToolChoice? other) =>
        ReferenceEquals(this, other)
        || (other is not null
            && Kind == other.Kind
            && string.Equals(ToolName, other.ToolName, StringComparison.Ordinal));

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as ToolChoice);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Kind, ToolName);

    /// <inheritdoc />
    public override string ToString() =>
        Kind == ToolChoiceKind.Named ? $"Named({ToolName})" : Kind.ToString();

    /// <summary>Returns whether two choices express the same policy and named tool.</summary>
    public static bool operator ==(ToolChoice? left, ToolChoice? right) =>
        Equals(left, right);

    /// <summary>Returns whether two choices express different policies or named tools.</summary>
    public static bool operator !=(ToolChoice? left, ToolChoice? right) =>
        !Equals(left, right);
}

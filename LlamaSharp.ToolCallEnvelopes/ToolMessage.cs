using LlamaSharp.ToolCallEnvelopes.Internal;

namespace LlamaSharp.ToolCallEnvelopes;

/// <summary>The role a message occupies in the host's native chat template.</summary>
public enum ToolMessageRole
{
    /// <summary>The single package-composed policy and output-contract message.</summary>
    System,
    /// <summary>A user or package repair-instruction message.</summary>
    User,
    /// <summary>A final answer, refusal, tool request, or rejected attempt from the assistant.</summary>
    Assistant,
    /// <summary>A result correlated with one validated tool call.</summary>
    Tool,
}

/// <summary>
/// An immutable prompt message with factories that prevent contradictory role data.
/// </summary>
public sealed class ToolMessage
{
    private static readonly IReadOnlyList<ToolCall> NoCalls = Array.Empty<ToolCall>();

    private ToolMessage(
        ToolMessageRole role,
        string content,
        IReadOnlyList<ToolCall>? calls = null,
        ToolCall? answeredCall = null,
        string? toolResultContent = null)
    {
        Role = role;
        Content = content;
        Calls = calls ?? NoCalls;
        AnsweredCall = answeredCall;
        ToolResultContent = toolResultContent;
    }

    /// <summary>The chat-template role.</summary>
    public ToolMessageRole Role { get; }

    /// <summary>The complete native-template message content.</summary>
    public string Content { get; }

    /// <summary>Calls represented by an assistant tool-request message.</summary>
    public IReadOnlyList<ToolCall> Calls { get; }

    /// <summary>The call answered by a tool-result message.</summary>
    public ToolCall? AnsweredCall { get; }

    internal string? ToolResultContent { get; }

    /// <summary>Creates one user message.</summary>
    public static ToolMessage User(string text) =>
        new(ToolMessageRole.User, RequireText(text, nameof(text)));

    /// <summary>Creates canonical history for a completed assistant answer.</summary>
    public static ToolMessage Assistant(string text)
    {
        RequireText(text, nameof(text));
        return new ToolMessage(
            ToolMessageRole.Assistant,
            CanonicalJson.SerializeAssistantMessage(text));
    }

    /// <summary>Creates canonical history for a validated assistant tool request.</summary>
    public static ToolMessage AssistantCalls(IEnumerable<ToolCall> calls)
    {
        Guard.NotNull(
            calls,
            nameof(calls),
            "The assistant call collection cannot be null. Supply the validated calls from a "
            + "ToolEnvelopeOutcome.ToolRequest.");
        var copy = calls.ToArray();
        if (copy.Length == 0)
        {
            throw new ArgumentException(
                "An assistant tool-request message needs at least one validated call. Use "
                + "ToolMessage.Assistant for a final answer instead of creating an empty request.",
                nameof(calls));
        }

        for (var index = 0; index < copy.Length; index++)
        {
            if (copy[index] is null)
            {
                throw new ArgumentException(
                    $"The assistant call collection contains null at index {index}. Supply only "
                    + "validated ToolCall values returned by this plan.",
                    nameof(calls));
            }
            if (copy[index].Index != index)
            {
                throw new ArgumentException(
                    $"The assistant call at collection index {index} reports call index "
                    + $"{copy[index].Index}. Call indexes must be contiguous and start at zero so "
                    + "tool results correlate with the exact model request.",
                    nameof(calls));
            }
        }

        var readOnly = Array.AsReadOnly(copy);
        return new ToolMessage(
            ToolMessageRole.Assistant,
            CanonicalJson.SerializeToolRequest(readOnly),
            readOnly);
    }

    /// <summary>Creates canonical history for the result of one validated call.</summary>
    public static ToolMessage ToolResult(ToolCall call, string content)
    {
        Guard.NotNull(
            call,
            nameof(call),
            "A validated ToolCall is required so this result can be correlated with its assistant "
            + "request. Supply the exact call returned by ToolEnvelopeOutcome.ToolRequest.");
        Guard.NotNull(
            content,
            nameof(content),
            "Tool-result content cannot be null because the next prompt needs an explicit result. "
            + "Use an empty string only when an intentionally empty result is meaningful.");
        return new ToolMessage(
            ToolMessageRole.Tool,
            CanonicalJson.SerializeToolResult(call, content),
            answeredCall: call,
            toolResultContent: content);
    }

    internal static ToolMessage System(string content) =>
        new(ToolMessageRole.System, content);

    internal static ToolMessage RawAssistant(string content) =>
        new(ToolMessageRole.Assistant, content);

    internal static ToolMessage AssistantRefusal(string reason) =>
        new(
            ToolMessageRole.Assistant,
            CanonicalJson.SerializeRefusal(reason));

    internal static ToolMessage PackageUser(string content) =>
        new(ToolMessageRole.User, content);

    private static string RequireText(string text, string parameterName)
    {
        Guard.NotNull(
            text,
            parameterName,
            "Message text cannot be null. Supply visible user or assistant text for this history "
            + "entry.");
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException(
                $"Message text contains {text.Length} character(s) but no visible content. Supply "
                + "at least one non-whitespace character.",
                parameterName);
        }
        return text;
    }
}

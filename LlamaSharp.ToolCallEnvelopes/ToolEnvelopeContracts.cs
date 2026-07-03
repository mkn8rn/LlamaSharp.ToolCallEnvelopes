using System.Text.Json;

namespace LlamaSharp.ToolCallEnvelopes;

/// <summary>
/// Describes one callable tool.
/// </summary>
public sealed record ToolDefinition(
    string Name,
    string Description,
    JsonElement ParametersSchema);

/// <summary>
/// A tool invocation contained in a completed envelope.
/// </summary>
public sealed record ToolCall(
    string Id,
    string Name,
    string ArgumentsJson);

/// <summary>
/// A streamed fragment of one tool invocation.
/// </summary>
public sealed record ToolCallDelta(
    int Index,
    string? Id,
    string? Name,
    string? ArgumentsFragment);

/// <summary>
/// A message supplied to the envelope prompt builder.
/// </summary>
public sealed record ToolAwareMessage
{
    public required string Role { get; init; }
    public string? Content { get; init; }
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }
    public string? ToolCallId { get; init; }
    public string? ImageBase64 { get; init; }
    public string? ImageMediaType { get; init; }
    public bool HasImage => ImageBase64 is not null;

    public static ToolAwareMessage System(string content) =>
        new() { Role = "system", Content = content };

    public static ToolAwareMessage User(string content) =>
        new() { Role = "user", Content = content };

    public static ToolAwareMessage Assistant(string content) =>
        new() { Role = "assistant", Content = content };

    public static ToolAwareMessage AssistantWithToolCalls(
        IReadOnlyList<ToolCall> toolCalls,
        string? content = null) =>
        new() { Role = "assistant", Content = content, ToolCalls = toolCalls };

    public static ToolAwareMessage ToolResult(string toolCallId, string content) =>
        new() { Role = "tool", ToolCallId = toolCallId, Content = content };

    public static ToolAwareMessage ToolResultWithImage(
        string toolCallId,
        string content,
        string imageBase64,
        string mediaType = "image/png") =>
        new()
        {
            Role = "tool",
            ToolCallId = toolCallId,
            Content = content,
            ImageBase64 = imageBase64,
            ImageMediaType = mediaType
        };
}

/// <summary>
/// Result produced by parsing a completed envelope.
/// </summary>
public sealed record ToolEnvelopeResult(
    string Mode,
    string? Content,
    IReadOnlyList<ToolCall> ToolCalls,
    string? Refusal)
{
    public bool HasToolCalls => ToolCalls.Count > 0;
}

/// <summary>
/// One streaming output produced while walking an envelope.
/// </summary>
public sealed record ToolEnvelopeStreamChunk(
    string? TextDelta,
    ToolCallDelta? ToolCallDelta)
{
    public bool IsTextDelta => TextDelta is not null;
    public bool IsToolCallDelta => ToolCallDelta is not null;

    public static ToolEnvelopeStreamChunk Text(string delta) => new(delta, null);
    public static ToolEnvelopeStreamChunk ToolCall(ToolCallDelta delta) => new(null, delta);
}

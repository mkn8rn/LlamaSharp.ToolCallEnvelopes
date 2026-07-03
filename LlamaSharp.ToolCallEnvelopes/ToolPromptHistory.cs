namespace LlamaSharp.ToolCallEnvelopes;

/// <summary>
/// Role for a prompt history message.
/// </summary>
public enum ToolPromptRole
{
    System = 0,
    User,
    Assistant,
}

/// <summary>
/// One role/content pair ready to map into LLamaSharp ChatHistory.
/// </summary>
public sealed record ToolPromptMessage(
    ToolPromptRole Role,
    string Content);

/// <summary>
/// Prompt history produced by the envelope prompt builder.
/// </summary>
public sealed record ToolPromptHistory(
    IReadOnlyList<ToolPromptMessage> Messages);

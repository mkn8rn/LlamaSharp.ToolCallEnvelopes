namespace LlamaSharp.ToolCallEnvelopes;

/// <summary>
/// Raised when an envelope cannot be parsed or does not match the contract.
/// </summary>
public sealed class LlamaSharpToolEnvelopeException : Exception
{
    public string PayloadPreview { get; }

    public LlamaSharpToolEnvelopeException(string message, string payload, Exception? inner = null)
        : base(message, inner)
    {
        PayloadPreview = payload[..Math.Min(payload.Length, 200)];
    }
}

/// <summary>
/// Raised when strict tool grammar generation cannot enforce a tool schema.
/// </summary>
public sealed class LlamaSharpToolSchemaException : Exception
{
    public string ToolName { get; }
    public IReadOnlyList<string> UnsupportedKeywords { get; }

    public LlamaSharpToolSchemaException(string toolName, IReadOnlyList<string> unsupportedKeywords)
        : base(BuildMessage(toolName, unsupportedKeywords))
    {
        ToolName = toolName;
        UnsupportedKeywords = unsupportedKeywords;
    }

    private static string BuildMessage(string toolName, IReadOnlyList<string> unsupportedKeywords)
    {
        if (unsupportedKeywords.Count == 0)
            return $"Tool '{toolName}' does not have a convertible object parameter schema.";

        return $"Tool '{toolName}' uses schema features that cannot be enforced: {string.Join(", ", unsupportedKeywords)}.";
    }
}

namespace LlamaSharp.ToolCallEnvelopes;

/// <summary>
/// Selects how a completed envelope is classified.
/// </summary>
public enum ToolEnvelopeMode
{
    /// <summary>
    /// Classify from the payload shape. Inferred prompts prefer the minimal
    /// payload, while inferred grammars and parsers also accept declared
    /// <c>mode</c>/<c>calls</c> envelopes.
    /// </summary>
    Inferred = 0,

    /// <summary>
    /// Require the declared <c>mode</c>, <c>text</c>, and
    /// <c>calls</c> fields to agree.
    /// </summary>
    StrictDeclared = 1,
}

/// <summary>
/// Controls whether the streaming walker checks semantic contradictions before
/// the final JSON document is complete.
/// </summary>
public enum ToolEnvelopeStreamValidation
{
    Off = 0,
    Strict = 1,
}

/// <summary>
/// Normalized result kinds returned by the envelope parser.
/// </summary>
public enum ToolEnvelopeResultMode
{
    Message = 0,
    ToolCalls,
    Refusal,
}

/// <summary>
/// Options for completed-envelope parsing.
/// </summary>
public sealed record ToolEnvelopeParserOptions
{
    public ToolEnvelopeMode EnvelopeMode { get; init; } = ToolEnvelopeMode.Inferred;

    /// <summary>
    /// Accepts the declared <c>calls</c> property in inferred mode.
    /// </summary>
    public bool AllowLegacyCalls { get; init; } = true;
}

/// <summary>
/// A non-throwing parse result for hosts that need to own retry and logging
/// policy.
/// </summary>
public sealed record ToolEnvelopeParseResult(
    bool Success,
    ToolEnvelopeResult? Value,
    LlamaSharpToolEnvelopeException? Error)
{
    public static ToolEnvelopeParseResult FromValue(ToolEnvelopeResult value) =>
        new(true, value, null);

    public static ToolEnvelopeParseResult FromError(LlamaSharpToolEnvelopeException error) =>
        new(false, null, error);
}

/// <summary>
/// Prompt options shared by the prompt builder and managed runner.
/// </summary>
public sealed record ToolPromptOptions
{
    public ToolChoice ToolChoice { get; init; } = ToolChoice.Auto;
    public ToolEnvelopeMode EnvelopeMode { get; init; } = ToolEnvelopeMode.Inferred;
    public bool StrictTools { get; init; }
    public bool AllowRefusal { get; init; }
    public int ImageCount { get; init; }
}

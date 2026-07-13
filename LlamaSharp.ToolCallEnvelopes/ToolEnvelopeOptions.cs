namespace LlamaSharp.ToolCallEnvelopes;

/// <summary>
/// Structural and memory limits used while compiling and running a plan.
/// </summary>
public sealed record ToolEnvelopeLimits
{
    /// <summary>Defaults intended for local models and constrained hosts.</summary>
    public static ToolEnvelopeLimits Constrained { get; } = new();

    /// <summary>Maximum tools in one compiled catalog.</summary>
    public int MaxTools { get; init; } = 16;
    /// <summary>Maximum UTF-16 characters in the generated semantic catalog.</summary>
    public int MaxCatalogPromptCharacters { get; init; } = 4_096;
    /// <summary>Maximum UTF-16 characters in one tool description.</summary>
    public int MaxToolDescriptionCharacters { get; init; } = 512;
    /// <summary>Maximum UTF-16 characters in one parameter description.</summary>
    public int MaxParameterDescriptionCharacters { get; init; } = 512;
    /// <summary>Maximum serialized UTF-16 characters in one tool schema.</summary>
    public int MaxToolSchemaCharacters { get; init; } = 32_768;
    /// <summary>Maximum nested instance depth represented by one schema.</summary>
    public int MaxSchemaDepth { get; init; } = 8;
    /// <summary>Maximum declared properties in any object schema.</summary>
    public int MaxPropertiesPerObject { get; init; } = 32;
    /// <summary>Maximum values in any enum constraint.</summary>
    public int MaxEnumValues { get; init; } = 64;
    /// <summary>Maximum combined serialized UTF-16 characters in one enum.</summary>
    public int MaxEnumTextCharacters { get; init; } = 4_096;
    /// <summary>Maximum compiled schema nodes for one tool.</summary>
    public int MaxSchemaRules { get; init; } = 256;
    /// <summary>Maximum accumulated UTF-16 characters in one model envelope.</summary>
    public int MaxEnvelopeCharacters { get; init; } = 65_536;
    /// <summary>Maximum Unicode scalar values in final assistant text.</summary>
    public int MaxFinalTextCharacters { get; init; } = 8_192;
    /// <summary>Maximum Unicode scalar values in a refusal reason.</summary>
    public int MaxRefusalCharacters { get; init; } = 1_024;
    /// <summary>Default and absolute Unicode-scalar limit for generated schema strings.</summary>
    public int MaxGeneratedStringCharacters { get; init; } = 2_048;
    /// <summary>Default and absolute item limit for generated schema arrays.</summary>
    public int MaxGeneratedArrayItems { get; init; } = 32;
    /// <summary>Maximum JSON characters in a generated schema number.</summary>
    public int MaxGeneratedNumberCharacters { get; init; } = 128;
    /// <summary>Maximum UTF-16 characters in one tool result added to history.</summary>
    public int MaxToolResultCharacters { get; init; } = 32_768;
    /// <summary>Maximum escaped UTF-16 characters retained in a diagnostic payload preview.</summary>
    public int MaxDiagnosticPreviewCharacters { get; init; } = 512;
}

/// <summary>Options that remain stable for the lifetime of a compiled plan.</summary>
public sealed record ToolEnvelopePlanOptions
{
    /// <summary>Whether a turn may return a refusal.</summary>
    public bool AllowRefusal { get; init; }

    /// <summary>Maximum tool requests in one model envelope.</summary>
    public int MaxCallsPerTurn { get; init; } = 1;

    /// <summary>Structural and memory limits for the plan.</summary>
    public ToolEnvelopeLimits Limits { get; init; } = ToolEnvelopeLimits.Constrained;
}

/// <summary>Stable costs measured while compiling a plan.</summary>
public sealed record ToolEnvelopePlanMetrics(
    string CatalogFingerprint,
    int ToolCount,
    int CatalogPromptCharacters,
    int SchemaRuleCount,
    int MaximumSchemaDepth);

/// <summary>Costs measured for one concrete turn.</summary>
public sealed record ToolEnvelopeTurnMetrics(
    int PromptCharacters,
    int GrammarCharacters);

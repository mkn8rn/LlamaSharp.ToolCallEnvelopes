namespace LlamaSharp.ToolCallEnvelopes;

/// <summary>Identifies a plan-compilation problem.</summary>
public enum ToolEnvelopePlanDiagnosticCode
{
    /// <summary>A plan or resource limit is missing or outside its accepted range.</summary>
    InvalidLimit,
    /// <summary>The catalog contains more tools than the configured limit.</summary>
    TooManyTools,
    /// <summary>Two catalog entries use the same exact tool name.</summary>
    DuplicateToolName,
    /// <summary>A tool or parameter description exceeds its configured limit.</summary>
    DescriptionTooLong,
    /// <summary>A serialized tool schema exceeds its configured limit.</summary>
    SchemaTooLarge,
    /// <summary>A schema node is not a well-formed member of the supported profile.</summary>
    InvalidSchema,
    /// <summary>A schema object repeats the same JSON property.</summary>
    DuplicateSchemaProperty,
    /// <summary>The root argument schema is not an object.</summary>
    NonObjectSchema,
    /// <summary>An object schema does not set <c>additionalProperties</c> to false.</summary>
    OpenObject,
    /// <summary>A schema uses a keyword or literal form outside the supported profile.</summary>
    UnsupportedKeyword,
    /// <summary>A schema constraint is malformed, contradictory, or unrepresentable.</summary>
    InvalidConstraint,
    /// <summary>A <c>required</c> declaration is malformed or names an unknown property.</summary>
    InvalidRequiredProperty,
    /// <summary>A local JSON Pointer reference is malformed or cannot be resolved.</summary>
    UnknownReference,
    /// <summary>A local reference would make the argument schema recursive.</summary>
    CircularReference,
    /// <summary>A schema instance path exceeds the configured depth limit.</summary>
    SchemaTooDeep,
    /// <summary>An object declares more properties than the configured limit.</summary>
    TooManyProperties,
    /// <summary>An enumeration contains more values than the configured limit.</summary>
    TooManyEnumValues,
    /// <summary>The serialized enumeration text exceeds the configured limit.</summary>
    EnumTextTooLong,
    /// <summary>A compiled schema requires more grammar rules than the configured limit.</summary>
    TooManySchemaRules,
    /// <summary>The model-facing semantic catalog exceeds the configured prompt limit.</summary>
    CatalogPromptTooLarge,
}

/// <summary>One precise plan-compilation diagnostic.</summary>
public sealed record ToolEnvelopePlanDiagnostic(
    ToolEnvelopePlanDiagnosticCode Code,
    string Message,
    string JsonPointer,
    string? ToolName = null);

/// <summary>Thrown when a catalog cannot produce one complete usable plan.</summary>
public sealed class ToolEnvelopePlanException : Exception
{
    internal ToolEnvelopePlanException(IReadOnlyList<ToolEnvelopePlanDiagnostic> diagnostics)
        : base(CreateMessage(diagnostics))
    {
        Diagnostics = diagnostics;
    }

    /// <summary>All diagnostics found during the compilation pass.</summary>
    public IReadOnlyList<ToolEnvelopePlanDiagnostic> Diagnostics { get; }

    private static string CreateMessage(IReadOnlyList<ToolEnvelopePlanDiagnostic> diagnostics)
    {
        const int displayedDiagnostics = 8;
        var details = diagnostics
            .Take(displayedDiagnostics)
            .Select((diagnostic, index) =>
            {
                var tool = diagnostic.ToolName is null
                    ? string.Empty
                    : $" for tool '{diagnostic.ToolName}'";
                var location = string.IsNullOrEmpty(diagnostic.JsonPointer)
                    ? ""
                    : $" at schema field '{diagnostic.JsonPointer}'";
                return $"Problem {index + 1}{tool}{location} ({diagnostic.Code}): "
                    + diagnostic.Message;
            });
        var omitted = diagnostics.Count > displayedDiagnostics
            ? $" {diagnostics.Count - displayedDiagnostics} additional problem(s) are available in "
              + "the Diagnostics property."
            : string.Empty;

        return "The tool-envelope plan cannot be compiled, so no prompt, grammar, parser, or "
            + "runner contract was created. "
            + string.Join(" ", details)
            + omitted
            + " Correct the catalog, schema, or plan limits identified above and compile the plan "
            + "again before starting model inference.";
    }
}

/// <summary>Identifies why model output was rejected.</summary>
public enum ToolEnvelopeErrorCode
{
    /// <summary>The response contains no non-whitespace text.</summary>
    EmptyOutput,
    /// <summary>The response exceeds <see cref="ToolEnvelopeLimits.MaxEnvelopeCharacters"/>.</summary>
    OutputTooLarge,
    /// <summary>The response is not one complete strict JSON value.</summary>
    MalformedJson,
    /// <summary>The response root is not a JSON object.</summary>
    ExpectedObject,
    /// <summary>An envelope, call, or argument object repeats a property.</summary>
    DuplicateProperty,
    /// <summary>An envelope object contains a field outside its exact shape.</summary>
    UnknownProperty,
    /// <summary>An envelope field has the wrong JSON value type.</summary>
    InvalidPropertyType,
    /// <summary>The final-text field contains no visible text.</summary>
    EmptyText,
    /// <summary>The final text exceeds its configured Unicode-character limit.</summary>
    TextTooLarge,
    /// <summary>The response uses a refusal branch that this turn does not permit.</summary>
    RefusalNotAllowed,
    /// <summary>The refusal field contains no visible text.</summary>
    EmptyRefusal,
    /// <summary>The refusal exceeds its configured Unicode-character limit.</summary>
    RefusalTooLarge,
    /// <summary>The response requests tools on a turn that permits only a final outcome.</summary>
    ToolCallsNotAllowed,
    /// <summary>The response returns a final outcome on a turn that requires a tool request.</summary>
    ToolCallsRequired,
    /// <summary>The tool-request array contains no calls.</summary>
    EmptyToolCalls,
    /// <summary>The tool-request array exceeds the turn's call limit.</summary>
    TooManyToolCalls,
    /// <summary>One tool-call object does not contain exactly <c>name</c> and <c>arguments</c>.</summary>
    InvalidToolCall,
    /// <summary>A requested tool name is not present in the compiled plan.</summary>
    UnknownTool,
    /// <summary>A named turn received a request for a different tool.</summary>
    WrongTool,
    /// <summary>A tool's arguments field is not a JSON object.</summary>
    InvalidArguments,
    /// <summary>A tool's argument object violates its compiled schema.</summary>
    SchemaViolation,
}

/// <summary>A bounded, typed explanation of rejected model output.</summary>
public sealed record ToolEnvelopeError(
    ToolEnvelopeErrorCode Code,
    string Message,
    string JsonPointer,
    string PayloadPreview);

/// <summary>Thrown by <see cref="ToolEnvelopeTurn.Parse"/> for invalid model output.</summary>
public sealed class ToolEnvelopeException : Exception
{
    internal ToolEnvelopeException(ToolEnvelopeError error, Exception? innerException = null)
        : base(error.Message, innerException)
    {
        Error = error;
    }

    /// <summary>The typed validation error.</summary>
    public ToolEnvelopeError Error { get; }
}

namespace LlamaSharp.ToolCallEnvelopes.Tests;

[TestFixture]
public sealed class PlanDiagnosticMessageTests
{
    public static IEnumerable<TestCaseData> EveryPlanDiagnostic =>
        Enum.GetValues<ToolEnvelopePlanDiagnosticCode>()
            .Select(code => new TestCaseData(code).SetName($"Descriptive_plan_{code}"));

    [TestCaseSource(nameof(EveryPlanDiagnostic))]
    public void EveryPlanDiagnostic_NamesTheProblemLocationAndRecovery(
        ToolEnvelopePlanDiagnosticCode expectedCode)
    {
        var compile = SetupFor(expectedCode);

        var exception = compile.Should().Throw<ToolEnvelopePlanException>().Which;
        var diagnostic = exception.Diagnostics.Should().Contain(item => item.Code == expectedCode)
            .Which;

        diagnostic.Message.Length.Should().BeGreaterThan(20);
        exception.Message.Should().Contain("cannot be compiled")
            .And.Contain($"({expectedCode})")
            .And.Contain(diagnostic.Message)
            .And.Contain("Correct the catalog, schema, or plan limits")
            .And.Contain("compile the plan again");
        if (!string.IsNullOrEmpty(diagnostic.JsonPointer))
            exception.Message.Should().Contain($"schema field '{diagnostic.JsonPointer}'");
    }

    private static Action SetupFor(ToolEnvelopePlanDiagnosticCode code) => code switch
    {
        ToolEnvelopePlanDiagnosticCode.InvalidLimit => () => TestCatalog.Plan(maxCalls: 0),
        ToolEnvelopePlanDiagnosticCode.TooManyTools => () => TestCatalog.Plan(
            tools: [TestCatalog.Weather(), TestCatalog.Search()],
            limits: ToolEnvelopeLimits.Constrained with { MaxTools = 1 }),
        ToolEnvelopePlanDiagnosticCode.DuplicateToolName => () => TestCatalog.Plan(
            tools: [TestCatalog.Weather(), TestCatalog.Weather("Second description.")]),
        ToolEnvelopePlanDiagnosticCode.DescriptionTooLong => () => TestCatalog.Plan(
            tools: [TestCatalog.Weather()],
            limits: ToolEnvelopeLimits.Constrained with { MaxToolDescriptionCharacters = 4 }),
        ToolEnvelopePlanDiagnosticCode.SchemaTooLarge => () => Compile(
            Closed("\"value\":{\"type\":\"string\"}"),
            ToolEnvelopeLimits.Constrained with { MaxToolSchemaCharacters = 10 }),
        ToolEnvelopePlanDiagnosticCode.InvalidSchema => () => Compile(
            "{\"properties\":{},\"additionalProperties\":false}"),
        ToolEnvelopePlanDiagnosticCode.DuplicateSchemaProperty => () => Compile(
            "{\"type\":\"object\",\"type\":\"object\",\"properties\":{},"
            + "\"additionalProperties\":false}"),
        ToolEnvelopePlanDiagnosticCode.NonObjectSchema => () => Compile(
            "{\"type\":\"string\"}"),
        ToolEnvelopePlanDiagnosticCode.OpenObject => () => Compile(
            "{\"type\":\"object\",\"properties\":{}}"),
        ToolEnvelopePlanDiagnosticCode.UnsupportedKeyword => () => Compile(
            Closed("\"value\":{\"type\":\"string\",\"pattern\":\"x\"}")),
        ToolEnvelopePlanDiagnosticCode.InvalidConstraint => () => Compile(
            Closed("\"value\":{\"type\":\"string\",\"minLength\":2,\"maxLength\":1}")),
        ToolEnvelopePlanDiagnosticCode.InvalidRequiredProperty => () => Compile(
            "{\"type\":\"object\",\"properties\":{},\"required\":[\"missing\"],"
            + "\"additionalProperties\":false}"),
        ToolEnvelopePlanDiagnosticCode.UnknownReference => () => Compile(
            Closed("\"value\":{\"$ref\":\"#/$defs/missing\"}")),
        ToolEnvelopePlanDiagnosticCode.CircularReference => () => Compile(
            "{\"type\":\"object\",\"$defs\":{\"node\":{\"$ref\":\"#/$defs/node\"}},"
            + "\"properties\":{\"node\":{\"$ref\":\"#/$defs/node\"}},"
            + "\"additionalProperties\":false}"),
        ToolEnvelopePlanDiagnosticCode.SchemaTooDeep => () => Compile(
            Closed(
                "\"outer\":{\"type\":\"object\",\"properties\":{"
                + "\"inner\":{\"type\":\"string\"}},\"additionalProperties\":false}"),
            ToolEnvelopeLimits.Constrained with { MaxSchemaDepth = 2 }),
        ToolEnvelopePlanDiagnosticCode.TooManyProperties => () => Compile(
            Closed("\"first\":{\"type\":\"string\"},\"second\":{\"type\":\"string\"}"),
            ToolEnvelopeLimits.Constrained with { MaxPropertiesPerObject = 1 }),
        ToolEnvelopePlanDiagnosticCode.TooManyEnumValues => () => Compile(
            Closed("\"value\":{\"type\":\"string\",\"enum\":[\"a\",\"b\"]}"),
            ToolEnvelopeLimits.Constrained with { MaxEnumValues = 1 }),
        ToolEnvelopePlanDiagnosticCode.EnumTextTooLong => () => Compile(
            Closed("\"value\":{\"type\":\"string\",\"enum\":[\"long\"]}"),
            ToolEnvelopeLimits.Constrained with { MaxEnumTextCharacters = 2 }),
        ToolEnvelopePlanDiagnosticCode.TooManySchemaRules => () => Compile(
            Closed("\"value\":{\"type\":\"string\"}"),
            ToolEnvelopeLimits.Constrained with { MaxSchemaRules = 1 }),
        ToolEnvelopePlanDiagnosticCode.CatalogPromptTooLarge => () => Compile(
            Closed("\"value\":{\"type\":\"string\"}"),
            ToolEnvelopeLimits.Constrained with { MaxCatalogPromptCharacters = 10 }),
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unmapped plan diagnostic."),
    };

    private static void Compile(
        string schema,
        ToolEnvelopeLimits? limits = null) =>
        TestCatalog.Plan(
            tools: [ToolDefinition.Parse("diagnostic", "Exercises one diagnostic.", schema)],
            limits: limits);

    private static string Closed(string properties) =>
        "{\"type\":\"object\",\"properties\":{" + properties
        + "},\"additionalProperties\":false}";
}

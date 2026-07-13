namespace LlamaSharp.ToolCallEnvelopes.Tests;

[TestFixture]
public sealed class SchemaLiteralContractTests
{
    public static IEnumerable<TestCaseData> InvalidUnicodeLiterals
    {
        get
        {
            foreach (var keyword in new[] { "enum", "const" })
                foreach (var escapedCodeUnit in new[] { "\\uD800", "\\uDC00" })
                {
                    yield return new TestCaseData(keyword, escapedCodeUnit)
                        .SetName($"Rejects_{keyword}_{escapedCodeUnit[2..]}");
                }
        }
    }

    public static IEnumerable<TestCaseData> CanonicalNumericLiterals
    {
        get
        {
            foreach (var keyword in new[] { "enum", "const" })
            {
                yield return Numeric(keyword, "integer", "10000", "1E4");
                yield return Numeric(keyword, "integer", "-10000", "-1E4");
                yield return Numeric(keyword, "number", "0.0001", "1E-4");
            }
        }
    }

    public static IEnumerable<TestCaseData> UnrepresentableNumericLiterals
    {
        get
        {
            foreach (var keyword in new[] { "enum", "const" })
                foreach (var type in new[] { "integer", "number" })
                {
                    yield return new TestCaseData(keyword, type)
                        .SetName($"Rejects_unrepresentable_{type}_{keyword}");
                }
        }
    }

    [TestCaseSource(nameof(InvalidUnicodeLiterals))]
    public void Compile_RejectsUnpairedSurrogatesInEveryStringLiteralConstraint(
        string keyword,
        string escapedCodeUnit)
    {
        var constraint = keyword == "enum"
            ? $"\"enum\":[\"{escapedCodeUnit}\"]"
            : $"\"const\":\"{escapedCodeUnit}\"";
        var schema = Closed($"\"value\":{{\"type\":\"string\",{constraint}}}");

        Action compile = () => TestCatalog.Plan(
            tools: [ToolDefinition.Parse("unicode", "Checks Unicode literals.", schema)]);

        var exception = compile.Should().Throw<ToolEnvelopePlanException>().Which;
        var expectedPointer = keyword == "enum"
            ? "/properties/value/enum/0"
            : "/properties/value/const";
        var expectedCodeUnit = escapedCodeUnit[2..];
        exception.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == ToolEnvelopePlanDiagnosticCode.InvalidConstraint
            && diagnostic.JsonPointer == expectedPointer
            && diagnostic.Message.Contains("valid Unicode scalar sequence", StringComparison.Ordinal)
            && diagnostic.Message.Contains(
                expectedCodeUnit,
                StringComparison.OrdinalIgnoreCase));
    }

    [TestCaseSource(nameof(CanonicalNumericLiterals))]
    public void Compile_NormalizesEveryNumericLiteralToItsShortestExactJson(
        string keyword,
        string type,
        string declared,
        string canonical)
    {
        var constraint = keyword == "enum"
            ? $"\"enum\":[{declared}]"
            : $"\"const\":{declared}";
        var schema = Closed($"\"value\":{{\"type\":\"{type}\",{constraint}}}", required: true);
        var turn = Turn(schema, maximumNumberCharacters: 4);

        turn.Grammar.Should().Contain(canonical).And.NotContain(declared);
        turn.Prompt[0].Content.Should().Contain(canonical).And.NotContain(declared);
        turn.Parse(TestCatalog.ToolRequest("literal", $"{{\"value\":{canonical}}}"))
            .Should().BeOfType<ToolEnvelopeOutcome.ToolRequest>();
        turn.TryParse(
            TestCatalog.ToolRequest("literal", $"{{\"value\":{declared}}}"),
            out _,
            out var error).Should().BeFalse();
        error!.Code.Should().Be(ToolEnvelopeErrorCode.SchemaViolation);
    }

    [TestCaseSource(nameof(UnrepresentableNumericLiterals))]
    public void Compile_RejectsNumericLiteralWhenNoExactRepresentationFitsTheLimit(
        string keyword,
        string type)
    {
        var constraint = keyword == "enum"
            ? "\"enum\":[12345]"
            : "\"const\":12345";
        var schema = Closed($"\"value\":{{\"type\":\"{type}\",{constraint}}}");

        Action compile = () => Turn(schema, maximumNumberCharacters: 4);

        compile.Should().Throw<ToolEnvelopePlanException>()
            .Which.Diagnostics.Should().Contain(diagnostic =>
                diagnostic.Code == ToolEnvelopePlanDiagnosticCode.InvalidConstraint
                && diagnostic.JsonPointer.StartsWith("/properties/value", StringComparison.Ordinal)
                && diagnostic.Message.Contains(
                    nameof(ToolEnvelopeLimits.MaxGeneratedNumberCharacters),
                    StringComparison.Ordinal));
    }

    [Test]
    public void Compile_KeepsRepresentableNumericEnumMembersAndRemovesImpossibleMembers()
    {
        var schema = Closed(
            "\"value\":{\"type\":\"integer\",\"enum\":[12345,2]}",
            required: true);
        var turn = Turn(schema, maximumNumberCharacters: 4);

        turn.Grammar.Should().Contain("\"2\"").And.NotContain("12345");
        turn.Parse(TestCatalog.ToolRequest("literal", "{\"value\":2}"))
            .Should().BeOfType<ToolEnvelopeOutcome.ToolRequest>();
        turn.TryParse(
            TestCatalog.ToolRequest("literal", "{\"value\":12345}"),
            out _,
            out var error).Should().BeFalse();
        error!.Code.Should().Be(ToolEnvelopeErrorCode.SchemaViolation);
    }

    private static ToolEnvelopeTurn Turn(string schema, int maximumNumberCharacters) =>
        TestCatalog.Turn(
            ToolChoice.Required,
            tools: [ToolDefinition.Parse("literal", "Checks literal constraints.", schema)],
            limits: ToolEnvelopeLimits.Constrained with
            {
                MaxGeneratedNumberCharacters = maximumNumberCharacters,
            });

    private static string Closed(string properties, bool required = false) =>
        $"{{\"type\":\"object\",\"properties\":{{{properties}}}"
        + (required ? ",\"required\":[\"value\"]" : string.Empty)
        + ",\"additionalProperties\":false}";

    private static TestCaseData Numeric(
        string keyword,
        string type,
        string declared,
        string canonical) =>
        new TestCaseData(keyword, type, declared, canonical)
            .SetName($"Normalizes_{type}_{keyword}_{declared.Replace('-', 'n').Replace('.', '_')}");
}

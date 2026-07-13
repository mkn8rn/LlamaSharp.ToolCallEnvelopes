using Json.Schema;

namespace LlamaSharp.ToolCallEnvelopes.Tests;

[TestFixture]
public sealed class SchemaKindCombinationTests
{
    private static readonly SchemaKindCase[] DeclaredKinds =
    [
        new("object", "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}"),
        new("array", "{\"type\":\"array\",\"items\":{\"type\":\"string\"}}"),
        new("string", "{\"type\":\"string\"}"),
        new("integer", "{\"type\":\"integer\"}"),
        new("number", "{\"type\":\"number\"}"),
        new("boolean", "{\"type\":\"boolean\"}"),
        new("null", "{\"type\":\"null\"}"),
    ];

    private static readonly JsonValueCase[] JsonValues =
    [
        new("object", "{}"),
        new("array", "[]"),
        new("string", "\"value\""),
        new("integer", "1"),
        new("fraction", "1.5"),
        new("true", "true"),
        new("false", "false"),
        new("null", "null"),
    ];

    public static IEnumerable<TestCaseData> EveryDeclaredKindAndJsonValue =>
        from declared in DeclaredKinds
        from value in JsonValues
        select new TestCaseData(declared, value)
            .SetName($"Schema_kind_{declared.Name}_value_{value.Name}");

    [TestCaseSource(nameof(EveryDeclaredKindAndJsonValue))]
    public void EveryDeclaredKindAndJsonValue_AgreesWithJsonSchema(
        SchemaKindCase declared,
        JsonValueCase value)
    {
        var schema = "{\"type\":\"object\",\"properties\":{\"value\":"
                     + declared.Schema
                     + "},\"required\":[\"value\"],\"additionalProperties\":false}";
        var reference = JsonSchema.FromText(schema);
        var turn = TestCatalog.Turn(
            ToolChoice.Required,
            tools: [ToolDefinition.Parse("kind_matrix", "Exercises every JSON kind.", schema)]);

        SchemaAgreement.AssertValidatesLike(
            reference,
            turn,
            "kind_matrix",
            $"{{\"value\":{value.Json}}}");
    }

    [Test]
    public void GeneratedKindMatrix_CoversEveryDeclaredAndObservedCombinationExactlyOnce()
    {
        var cases = EveryDeclaredKindAndJsonValue.ToArray();

        cases.Should().HaveCount(DeclaredKinds.Length * JsonValues.Length);
        cases.Select(test => test.TestName).Should().OnlyHaveUniqueItems();
    }

    public sealed record SchemaKindCase(string Name, string Schema);

    public sealed record JsonValueCase(string Name, string Json);
}

using Json.Schema;

namespace LlamaSharp.ToolCallEnvelopes.Tests;

internal static class SchemaAgreement
{
    internal static void AssertValidatesLike(
        JsonSchema reference,
        ToolEnvelopeTurn turn,
        string toolName,
        string arguments)
    {
        var expected = reference.Evaluate(TestCatalog.Json(arguments)).IsValid;
        var actual = turn.TryParse(
            TestCatalog.ToolRequest(toolName, arguments),
            out _,
            out _);

        actual.Should().Be(expected, arguments);
    }
}

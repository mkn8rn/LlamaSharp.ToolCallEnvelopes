using FluentAssertions;

namespace LlamaSharp.ToolCallEnvelopes.Tests;

[TestFixture]
public sealed class ToolDefinitionValidatorTests
{
    [Test]
    public void Validate_ReportsUnknownRequiredProperty()
    {
        var tool = ToolDefinition.FromJsonSchema(
            "lookup",
            "Looks up a value.",
            """
            {
              "type": "object",
              "properties": { "query": { "type": "string" } },
              "required": ["missing"]
            }
            """);

        var result = ToolDefinitionValidator.Validate([tool]);

        result.Success.Should().BeFalse();
        result.Errors.Should().Contain(error => error.JsonPath == "$.required[0]");
    }

    [Test]
    public void Validate_StrictProfileReportsUnsupportedSchemaFeature()
    {
        var tool = ToolDefinition.FromJsonSchema(
            "choose",
            "Chooses a value.",
            """{"type":"object","properties":{"value":{"oneOf":[{"type":"string"},{"type":"integer"}]}}}""");

        var result = ToolDefinitionValidator.Validate(
            [tool],
            new ToolDefinitionValidationOptions
            {
                RejectUnsupportedJsonSchemaKeywords = true,
            });

        result.Success.Should().BeFalse();
        result.Errors.Should().Contain(error => error.JsonPath.Contains("oneOf", StringComparison.Ordinal));
    }
}


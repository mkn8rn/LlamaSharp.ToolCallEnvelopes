namespace LlamaSharp.ToolCallEnvelopes.Tests;

[TestFixture]
public sealed class SemanticCatalogTests
{
    [Test]
    public void ReferenceSiteDescriptions_RemainDistinctAtEveryUse()
    {
        const string schema =
            """
            {
              "type":"object",
              "$defs":{"code":{"type":"string","description":"Definition text."}},
              "properties":{
                "origin":{"$ref":"#/$defs/code","description":"Origin code."},
                "destination":{"$ref":"#/$defs/code","description":"Destination code."}
              },
              "additionalProperties":false
            }
            """;
        var turn = TestCatalog.Turn(
            ToolChoice.Required,
            tools: [ToolDefinition.Parse("route", "Builds a route.", schema)]);

        var catalog = turn.Prompt[0].Content;

        catalog.Should().Contain("arg $[\"origin\"]: string, optional, length 0..2048; \"Origin code.\"")
            .And.Contain("arg $[\"destination\"]: string, optional, length 0..2048; \"Destination code.\"")
            .And.NotContain("Definition text.");
    }

    [Test]
    public void Catalog_DescribesEveryNestedArrayLevelAndConditionalRequirement()
    {
        const string schema =
            """
            {
              "type":"object",
              "properties":{
                "matrix":{
                  "type":"array",
                  "minItems":1,
                  "maxItems":2,
                  "items":{
                    "type":"array",
                    "minItems":2,
                    "maxItems":3,
                    "items":{
                      "type":"object",
                      "properties":{
                        "value":{"type":"integer","minimum":0,"maximum":9}
                      },
                      "required":["value"],
                      "additionalProperties":false
                    }
                  }
                }
              },
              "additionalProperties":false
            }
            """;
        var turn = TestCatalog.Turn(
            ToolChoice.Required,
            tools: [ToolDefinition.Parse("matrix", "Accepts a matrix.", schema)]);

        var catalog = turn.Prompt[0].Content;

        catalog.Should().Contain("arg $[\"matrix\"]: array of array of object, optional, items 1..2")
            .And.Contain("item $[\"matrix\"][]: array of object, items 2..3")
            .And.Contain("item $[\"matrix\"][][]: object")
            .And.Contain(
                "arg $[\"matrix\"][][][\"value\"]: integer, required when its parent is present, min 0, max 9");
    }

    [Test]
    public void ReferenceAnnotations_ReceiveTheSameValidationAsInlineAnnotations()
    {
        const string schema =
            """
            {
              "type":"object",
              "$defs":{"code":{"type":"string"}},
              "properties":{
                "value":{"$ref":"#/$defs/code","description":"12345"}
              },
              "additionalProperties":false
            }
            """;
        var tool = ToolDefinition.Parse("reference", "Tests a reference.", schema);
        var limits = ToolEnvelopeLimits.Constrained with
        {
            MaxParameterDescriptionCharacters = 4,
        };

        var compile = () => TestCatalog.Plan(tools: [tool], limits: limits);

        compile.Should().Throw<ToolEnvelopePlanException>()
            .Which.Diagnostics.Should().ContainSingle(diagnostic =>
                diagnostic.Code == ToolEnvelopePlanDiagnosticCode.DescriptionTooLong
                && diagnostic.JsonPointer == "/properties/value/description");
    }
}

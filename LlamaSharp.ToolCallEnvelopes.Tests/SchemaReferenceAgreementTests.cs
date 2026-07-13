using Json.Schema;

namespace LlamaSharp.ToolCallEnvelopes.Tests;

[TestFixture]
public sealed class SchemaReferenceAgreementTests
{
    public static IEnumerable<TestCaseData> AgreementCorpus
    {
        get
        {
            var cases = new (string Name, string Arguments)[]
            {
                ("minimal valid", """{"city":"Zagreb","unit":"celsius"}"""),
                ("all valid", """{"city":"Zagreb","unit":"fahrenheit","days":7,"include_alerts":false,"tags":["a","b"],"coordinates":{"latitude":45.8,"longitude":15.9}}"""),
                ("property order valid", """{"unit":"celsius","city":"Split"}"""),
                ("missing city", """{"unit":"celsius"}"""),
                ("missing unit", """{"city":"Zagreb"}"""),
                ("blank city", """{"city":"","unit":"celsius"}"""),
                ("long city", $$"""{"city":"{{new string('x', 65)}}","unit":"celsius"}"""),
                ("bad enum", """{"city":"Zagreb","unit":"kelvin"}"""),
                ("integer low", """{"city":"Zagreb","unit":"celsius","days":0}"""),
                ("integer high", """{"city":"Zagreb","unit":"celsius","days":8}"""),
                ("integer type", """{"city":"Zagreb","unit":"celsius","days":1.5}"""),
                ("boolean type", """{"city":"Zagreb","unit":"celsius","include_alerts":"yes"}"""),
                ("array min", """{"city":"Zagreb","unit":"celsius","tags":[]}"""),
                ("array max", """{"city":"Zagreb","unit":"celsius","tags":["a","b","c"]}"""),
                ("array too large", """{"city":"Zagreb","unit":"celsius","tags":["a","b","c","d"]}"""),
                ("array item type", """{"city":"Zagreb","unit":"celsius","tags":[1]}"""),
                ("nested valid", """{"city":"Zagreb","unit":"celsius","coordinates":{"latitude":45.8,"longitude":15.9}}"""),
                ("nested missing", """{"city":"Zagreb","unit":"celsius","coordinates":{"latitude":45.8}}"""),
                ("nested low", """{"city":"Zagreb","unit":"celsius","coordinates":{"latitude":-91,"longitude":15.9}}"""),
                ("nested high", """{"city":"Zagreb","unit":"celsius","coordinates":{"latitude":45.8,"longitude":181}}"""),
                ("nested extra", """{"city":"Zagreb","unit":"celsius","coordinates":{"latitude":45.8,"longitude":15.9,"altitude":120}}"""),
                ("root extra", """{"city":"Zagreb","unit":"celsius","unknown":true}"""),
            };

            foreach (var (name, arguments) in cases)
                yield return new TestCaseData(arguments).SetName($"Reference_agreement_{name.Replace(' ', '_')}");
        }
    }

    [TestCaseSource(nameof(AgreementCorpus))]
    public void PostValidator_AgreesWithJsonSchemaNetForTheAdmittedProfile(string arguments)
    {
        var reference = JsonSchema.FromText(TestCatalog.WeatherSchema);
        var turn = TestCatalog.Turn(ToolChoice.Required);

        SchemaAgreement.AssertValidatesLike(reference, turn, "get_weather", arguments);
    }

    [Test]
    public void PostValidator_AgreesForConstNullBooleanAndLocalReferences()
    {
        const string schema =
            """
            {
              "type":"object",
              "$defs":{
                "item":{
                  "type":"object",
                  "properties":{
                    "code":{"type":"string","const":"HR"},
                    "enabled":{"type":"boolean"},
                    "nothing":{"type":"null"}
                  },
                  "required":["code","enabled","nothing"],
                  "additionalProperties":false
                }
              },
              "properties":{"item":{"$ref":"#/$defs/item"}},
              "required":["item"],
              "additionalProperties":false
            }
            """;
        var cases = new[]
        {
            """{"item":{"code":"HR","enabled":true,"nothing":null}}""",
            """{"item":{"code":"US","enabled":true,"nothing":null}}""",
            """{"item":{"code":"HR","enabled":1,"nothing":null}}""",
            """{"item":{"code":"HR","enabled":false,"nothing":"null"}}""",
        };
        var reference = JsonSchema.FromText(schema);
        var tool = ToolDefinition.Parse("submit", "Submits an item.", schema);
        var turn = TestCatalog.Turn(ToolChoice.Required, tools: [tool]);

        foreach (var arguments in cases)
            SchemaAgreement.AssertValidatesLike(reference, turn, "submit", arguments);
    }

    [Test]
    public void PostValidator_AddsDuplicateKeyProtectionBeforeDispatch()
    {
        var turn = TestCatalog.Turn(ToolChoice.Required);
        var raw = TestCatalog.ToolRequest(
            "get_weather",
            """{"city":"Zagreb","city":"Split","unit":"celsius"}""");

        turn.TryParse(raw, out _, out var error).Should().BeFalse();

        error!.Code.Should().Be(ToolEnvelopeErrorCode.SchemaViolation);
        error.JsonPointer.Should().Be("/tool_calls/0/arguments/city");
    }
}

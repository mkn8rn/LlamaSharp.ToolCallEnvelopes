using System.Text.Json;

namespace LlamaSharp.ToolCallEnvelopes.Tests;

[TestFixture]
public sealed class ToolEnvelopeTurnParserTests
{
    public static IEnumerable<TestCaseData> InvalidEnvelopeShapes
    {
        get
        {
            yield return Invalid("", ToolEnvelopeErrorCode.EmptyOutput, "empty");
            yield return Invalid(" \r\n ", ToolEnvelopeErrorCode.EmptyOutput, "whitespace");
            yield return Invalid("[1]", ToolEnvelopeErrorCode.ExpectedObject, "array root");
            yield return Invalid("null", ToolEnvelopeErrorCode.ExpectedObject, "null root");
            yield return Invalid("\"text\"", ToolEnvelopeErrorCode.ExpectedObject, "string root");
            yield return Invalid("true", ToolEnvelopeErrorCode.ExpectedObject, "boolean root");
            yield return Invalid("{", ToolEnvelopeErrorCode.MalformedJson, "malformed json");
            yield return Invalid("{/*comment*/\"text\":\"ok\"}", ToolEnvelopeErrorCode.MalformedJson, "json comment");
            yield return Invalid("{\"text\":\"ok\",}", ToolEnvelopeErrorCode.MalformedJson, "trailing comma");
            yield return Invalid("{\"text\":\"one\"}{\"text\":\"two\"}", ToolEnvelopeErrorCode.MalformedJson, "multiple roots");
            yield return Invalid("\uFEFF{\"text\":\"ok\"}", ToolEnvelopeErrorCode.MalformedJson, "unicode bom");
            yield return Invalid("{}", ToolEnvelopeErrorCode.UnknownProperty, "empty object");
            yield return Invalid("{\"message\":\"hello\"}", ToolEnvelopeErrorCode.UnknownProperty, "unknown root");
            yield return Invalid("{\"text\":\"a\",\"refusal\":\"b\"}", ToolEnvelopeErrorCode.UnknownProperty, "mixed branches");
            yield return Invalid("{\"text\":\"a\",\"text\":\"b\"}", ToolEnvelopeErrorCode.DuplicateProperty, "duplicate root");
            yield return Invalid("{\"text\":1}", ToolEnvelopeErrorCode.InvalidPropertyType, "numeric text");
            yield return Invalid("{\"text\":\" \\t\"}", ToolEnvelopeErrorCode.EmptyText, "blank text");
            yield return Invalid("{\"refusal\":\"no\"}", ToolEnvelopeErrorCode.RefusalNotAllowed, "disabled refusal");
            yield return Invalid("{\"tool_calls\":{}}", ToolEnvelopeErrorCode.InvalidPropertyType, "object calls");
            yield return Invalid("{\"tool_calls\":null}", ToolEnvelopeErrorCode.InvalidPropertyType, "null calls");
            yield return Invalid("{\"tool_calls\":[]}", ToolEnvelopeErrorCode.EmptyToolCalls, "empty calls");
            yield return Invalid("{\"tool_calls\":[1]}", ToolEnvelopeErrorCode.InvalidToolCall, "primitive call");
            yield return Invalid("{\"tool_calls\":[{}]}", ToolEnvelopeErrorCode.InvalidToolCall, "empty call");
            yield return Invalid("{\"tool_calls\":[{\"name\":\"get_weather\"}]}", ToolEnvelopeErrorCode.InvalidToolCall, "missing arguments");
            yield return Invalid("{\"tool_calls\":[{\"arguments\":{}}]}", ToolEnvelopeErrorCode.InvalidToolCall, "missing name");
            yield return Invalid("{\"tool_calls\":[{\"name\":\"get_weather\",\"arguments\":{},\"extra\":1}]}", ToolEnvelopeErrorCode.InvalidToolCall, "extra call property");
            yield return Invalid("{\"tool_calls\":[{\"name\":\"get_weather\",\"name\":\"search\",\"arguments\":{}}]}", ToolEnvelopeErrorCode.DuplicateProperty, "duplicate call property");
            yield return Invalid("{\"tool_calls\":[{\"name\":1,\"arguments\":{}}]}", ToolEnvelopeErrorCode.InvalidPropertyType, "numeric name");
            yield return Invalid("{\"tool_calls\":[{\"name\":\"missing\",\"arguments\":{}}]}", ToolEnvelopeErrorCode.UnknownTool, "unknown tool");
            yield return Invalid("{\"tool_calls\":[{\"name\":\"get_weather\",\"arguments\":[]}]}", ToolEnvelopeErrorCode.InvalidArguments, "array arguments");
            yield return Invalid("{\"tool_calls\":[{\"name\":\"get_weather\",\"arguments\":{\"city\":\"Zagreb\"}}]}", ToolEnvelopeErrorCode.SchemaViolation, "missing required argument");
            yield return Invalid("{\"tool_calls\":[{\"name\":\"get_weather\",\"arguments\":{\"city\":\"Zagreb\",\"unit\":\"kelvin\"}}]}", ToolEnvelopeErrorCode.SchemaViolation, "enum violation");
            yield return Invalid("{\"tool_calls\":[{\"name\":\"get_weather\",\"arguments\":{\"city\":\"Zagreb\",\"unit\":\"celsius\",\"extra\":true}}]}", ToolEnvelopeErrorCode.SchemaViolation, "unknown argument");
            yield return Invalid("{\"tool_calls\":[{\"name\":\"get_weather\",\"arguments\":{\"city\":\"Zagreb\",\"city\":\"Split\",\"unit\":\"celsius\"}}]}", ToolEnvelopeErrorCode.SchemaViolation, "duplicate argument");
            yield return Invalid("{\"mode\":\"tool_calls\",\"calls\":[]}", ToolEnvelopeErrorCode.UnknownProperty, "unsupported discriminator");
            yield return Invalid("{\"calls\":[]}", ToolEnvelopeErrorCode.UnknownProperty, "unsupported call array");
            yield return Invalid("{\"content\":\"hello\"}", ToolEnvelopeErrorCode.UnknownProperty, "unsupported content property");
            yield return Invalid("{\"tool_calls\":[{\"id\":\"call_1\",\"name\":\"get_weather\",\"arguments\":{}}]}", ToolEnvelopeErrorCode.InvalidToolCall, "model-created id");
            yield return Invalid("{\"tool_calls\":[{\"name\":\"get_weather\",\"args\":{}}]}", ToolEnvelopeErrorCode.InvalidToolCall, "argument alias");
        }
    }

    [Test]
    public void Parse_AcceptsEveryLegalOutcomeForAuto()
    {
        var turn = TestCatalog.Turn(allowRefusal: true);

        var message = turn.Parse("""{"text":"It is sunny."}""");
        var request = turn.Parse(TestCatalog.ToolRequest(
            "get_weather",
            """{"unit":"celsius","city":"Zagreb","days":3,"include_alerts":true,"tags":["city"],"coordinates":{"longitude":15.98,"latitude":45.81}}"""));
        var refusal = turn.Parse("""{"refusal":"I cannot answer that."}""");

        message.Should().BeOfType<ToolEnvelopeOutcome.AssistantMessage>()
            .Which.Text.Should().Be("It is sunny.");
        var call = request.Should().BeOfType<ToolEnvelopeOutcome.ToolRequest>()
            .Which.Calls.Should().ContainSingle().Which;
        call.Index.Should().Be(0);
        call.Name.Should().Be("get_weather");
        call.Arguments.GetProperty("city").GetString().Should().Be("Zagreb");
        refusal.Should().BeOfType<ToolEnvelopeOutcome.Refusal>()
            .Which.Reason.Should().Be("I cannot answer that.");
    }

    [Test]
    public void Parse_AcceptsWhitespaceAndCallPropertyOrderButReturnsCanonicalData()
    {
        var turn = TestCatalog.Turn(ToolChoice.Required);
        const string raw =
            """
            {
              "tool_calls": [
                {
                  "arguments": {"unit":"celsius","city":"Zagreb"},
                  "name": "get_weather"
                }
              ]
            }
            """;

        var request = (ToolEnvelopeOutcome.ToolRequest)turn.Parse(raw);

        request.Calls[0].Arguments.GetProperty("unit").GetString().Should().Be("celsius");
    }

    [Test]
    public void Parse_AcceptsEscapedNamesAndSurroundingJsonWhitespace()
    {
        var finalTurn = TestCatalog.Turn(ToolChoice.None);
        var toolTurn = TestCatalog.Turn(ToolChoice.Required);

        var message = finalTurn.Parse(" \r\n{\"\\u0074ext\":\"done\"}\t");
        var request = toolTurn.Parse(
            "{\"tool_calls\":[{\"name\":\"get_weath\\u0065r\","
            + "\"arguments\":{\"city\":\"Zagreb\",\"unit\":\"celsius\"}}]}");

        message.Should().BeOfType<ToolEnvelopeOutcome.AssistantMessage>()
            .Which.Text.Should().Be("done");
        request.Should().BeOfType<ToolEnvelopeOutcome.ToolRequest>()
            .Which.Calls.Should().ContainSingle()
            .Which.Name.Should().Be("get_weather");
    }

    [Test]
    public void Parse_RejectsJsonThatExceedsTheTurnDepthBudget()
    {
        var turn = TestCatalog.Turn();
        var nested = new string('[', 20) + "0" + new string(']', 20);
        var output = $"{{\"text\":{nested}}}";

        turn.TryParse(output, out _, out var error).Should().BeFalse();

        error!.Code.Should().Be(ToolEnvelopeErrorCode.MalformedJson);
        error.Message.Should().Contain("maximum configured depth")
            .And.Contain("Recovery:");
    }

    [Test]
    public void Parse_ReportsRefusalTypeOnlyAfterConfirmingRefusalIsEnabled()
    {
        var turn = TestCatalog.Turn(ToolChoice.None, allowRefusal: true);

        turn.TryParse("{\"refusal\":42}", out _, out var error).Should().BeFalse();

        error!.Code.Should().Be(ToolEnvelopeErrorCode.InvalidPropertyType);
        error.JsonPointer.Should().Be("/refusal");
    }

    [Test]
    public void Parse_EnforcesEveryChoicePolicy()
    {
        var required = TestCatalog.Turn(ToolChoice.Required);
        var none = TestCatalog.Turn(ToolChoice.None, allowRefusal: true);
        var named = TestCatalog.Turn(
            ToolChoice.Named("search"),
            tools: [TestCatalog.Weather(), TestCatalog.Search()]);

        Reject(required, """{"text":"answer"}""", ToolEnvelopeErrorCode.ToolCallsRequired);
        Reject(required, """{"refusal":"no"}""", ToolEnvelopeErrorCode.RefusalNotAllowed);
        Reject(
            none,
            TestCatalog.ToolRequest(
                "get_weather",
                """{"city":"Zagreb","unit":"celsius"}"""),
            ToolEnvelopeErrorCode.ToolCallsNotAllowed);
        Reject(
            named,
            TestCatalog.ToolRequest(
                "get_weather",
                """{"city":"Zagreb","unit":"celsius"}"""),
            ToolEnvelopeErrorCode.WrongTool);

        named.Parse(TestCatalog.ToolRequest("search", """{"query":"weather"}"""))
            .Should().BeOfType<ToolEnvelopeOutcome.ToolRequest>();
        none.Parse("""{"text":"answer"}""")
            .Should().BeOfType<ToolEnvelopeOutcome.AssistantMessage>();
        none.Parse("""{"refusal":"no"}""")
            .Should().BeOfType<ToolEnvelopeOutcome.Refusal>();
    }

    [Test]
    public void Parse_EnforcesCallTextRefusalAndEnvelopeBounds()
    {
        var limits = ToolEnvelopeLimits.Constrained with
        {
            MaxEnvelopeCharacters = 200,
            MaxFinalTextCharacters = 4,
            MaxRefusalCharacters = 3,
        };
        var turn = TestCatalog.Turn(allowRefusal: true, limits: limits);

        Reject(turn, """{"text":"12345"}""", ToolEnvelopeErrorCode.TextTooLarge);
        Reject(turn, """{"refusal":"1234"}""", ToolEnvelopeErrorCode.RefusalTooLarge);
        Reject(turn, new string('x', 201), ToolEnvelopeErrorCode.OutputTooLarge);
    }

    [Test]
    public void Parse_AssignsIndexesAndHonorsTheCallCountBound()
    {
        var turn = TestCatalog.Turn(maxCalls: 2);
        const string twoCalls =
            """
            {"tool_calls":[
              {"name":"get_weather","arguments":{"city":"Zagreb","unit":"celsius"}},
              {"name":"get_weather","arguments":{"city":"Split","unit":"fahrenheit"}}
            ]}
            """;
        const string threeCalls =
            """
            {"tool_calls":[
              {"name":"get_weather","arguments":{"city":"Zagreb","unit":"celsius"}},
              {"name":"get_weather","arguments":{"city":"Split","unit":"fahrenheit"}},
              {"name":"get_weather","arguments":{"city":"Rijeka","unit":"celsius"}}
            ]}
            """;

        var request = (ToolEnvelopeOutcome.ToolRequest)turn.Parse(twoCalls);

        request.Calls.Select(call => call.Index).Should().Equal(0, 1);
        Reject(turn, threeCalls, ToolEnvelopeErrorCode.TooManyToolCalls);
    }

    [TestCaseSource(nameof(InvalidEnvelopeShapes))]
    public void TryParse_RejectsEveryInvalidStrictJsonShape(
        string output,
        ToolEnvelopeErrorCode expectedCode)
    {
        var turn = TestCatalog.Turn();

        var success = turn.TryParse(output, out var outcome, out var error);

        success.Should().BeFalse();
        outcome.Should().BeNull();
        error.Should().NotBeNull();
        error!.Code.Should().Be(expectedCode);
    }

    [Test]
    public void Parse_ThrowsTheSameTypedErrorAsTryParse()
    {
        var turn = TestCatalog.Turn();
        const string output = """{"tool_calls":[{"name":"missing","arguments":{}}]}""";

        turn.TryParse(output, out var outcome, out var error).Should().BeFalse();
        Action act = () => turn.Parse(output);
        var exception = act.Should().Throw<ToolEnvelopeException>().Which;

        outcome.Should().BeNull();
        exception.Error.Should().Be(error);
        exception.Error.JsonPointer.Should().Be("/tool_calls/0/name");
    }

    [Test]
    public void TryParse_ReportsAnUnknownNameBeforeInspectingItsArguments()
    {
        var turn = TestCatalog.Turn(ToolChoice.Required);
        const string output = """{"tool_calls":[{"name":"missing","arguments":null}]}""";

        turn.TryParse(output, out _, out var error).Should().BeFalse();

        error!.Code.Should().Be(ToolEnvelopeErrorCode.UnknownTool);
        error.JsonPointer.Should().Be("/tool_calls/0/name");
    }

    [Test]
    public void TryParse_NullIsAProgrammingError()
    {
        var turn = TestCatalog.Turn();

        var act = () => turn.TryParse(null!, out _, out _);

        act.Should().Throw<ArgumentNullException>().WithParameterName("output");
    }

    [Test]
    public void ErrorPreview_IsBoundedAndEscapesLogBreakingWhitespace()
    {
        var limits = ToolEnvelopeLimits.Constrained with
        {
            MaxDiagnosticPreviewCharacters = 12,
        };
        var turn = TestCatalog.Turn(limits: limits);

        turn.TryParse("{\n\t\"bad\":123456789}", out _, out var error).Should().BeFalse();

        error!.PayloadPreview.Length.Should().BeLessThanOrEqualTo(12);
        error.PayloadPreview.Should().NotContain("\n").And.NotContain("\t");
    }

    private static void Reject(
        ToolEnvelopeTurn turn,
        string output,
        ToolEnvelopeErrorCode expectedCode)
    {
        turn.TryParse(output, out var outcome, out var error).Should().BeFalse();
        outcome.Should().BeNull();
        error!.Code.Should().Be(expectedCode);
    }

    private static TestCaseData Invalid(
        string output,
        ToolEnvelopeErrorCode code,
        string name) =>
        new TestCaseData(output, code).SetName($"Rejects_{name.Replace(' ', '_')}");
}

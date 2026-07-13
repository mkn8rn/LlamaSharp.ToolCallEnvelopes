namespace LlamaSharp.ToolCallEnvelopes.Tests;

[TestFixture]
public sealed class UnicodeContractTests
{
    [TestCase("literal", "😀")]
    [TestCase("escaped", "\\uD83D\\uDE00")]
    public void OneUnicodeScalar_SatisfiesOneCharacterTextAndSchemaLimits(
        string representation,
        string jsonValue)
    {
        var limits = ToolEnvelopeLimits.Constrained with
        {
            MaxFinalTextCharacters = 1,
            MaxRefusalCharacters = 1,
        };
        var textTurn = TestCatalog.Turn(ToolChoice.None, limits: limits);
        var refusalTurn = TestCatalog.Turn(
            ToolChoice.None,
            allowRefusal: true,
            limits: limits);
        var schema =
            """
            {
              "type":"object",
              "properties":{"value":{"type":"string","minLength":1,"maxLength":1}},
              "required":["value"],
              "additionalProperties":false
            }
            """;
        var toolTurn = TestCatalog.Turn(
            ToolChoice.Required,
            tools: [ToolDefinition.Parse("unicode", "Accepts one character.", schema)]);

        textTurn.Parse($"{{\"text\":\"{jsonValue}\"}}")
            .Should().BeOfType<ToolEnvelopeOutcome.AssistantMessage>()
            .Which.Text.EnumerateRunes().Should().ContainSingle(representation);
        refusalTurn.Parse($"{{\"refusal\":\"{jsonValue}\"}}")
            .Should().BeOfType<ToolEnvelopeOutcome.Refusal>()
            .Which.Reason.EnumerateRunes().Should().ContainSingle(representation);
        toolTurn.Parse(TestCatalog.ToolRequest("unicode", $"{{\"value\":\"{jsonValue}\"}}"))
            .Should().BeOfType<ToolEnvelopeOutcome.ToolRequest>();
        toolTurn.Grammar.Should().Contain("unicode-pair")
            .And.Contain("unicode-bmp");
    }

    [TestCase("text", "{\"text\":\"\\uD800\"}", "/text")]
    [TestCase("text-low", "{\"text\":\"\\uDC00\"}", "/text")]
    [TestCase("refusal", "{\"refusal\":\"\\uD800x\"}", "/refusal")]
    public void FinalStrings_RejectEveryLoneEscapedSurrogate(
        string name,
        string output,
        string pointer)
    {
        var turn = TestCatalog.Turn(
            ToolChoice.None,
            allowRefusal: true);

        turn.TryParse(output, out _, out var error).Should().BeFalse(name);

        error!.Code.Should().Be(ToolEnvelopeErrorCode.MalformedJson);
        error.JsonPointer.Should().Be(pointer);
        error.Message.Should().Contain("surrogate")
            .And.Contain("Recovery:")
            .And.Contain("retry with bounded backpressure");
    }

    [Test]
    public void ToolString_RejectsLoneEscapedSurrogateBeforeDispatch()
    {
        var turn = TestCatalog.Turn(ToolChoice.Required);
        var output = TestCatalog.ToolRequest(
            "get_weather",
            """{"city":"\uD800","unit":"celsius"}""");

        turn.TryParse(output, out _, out var error).Should().BeFalse();

        error!.Code.Should().Be(ToolEnvelopeErrorCode.SchemaViolation);
        error.JsonPointer.Should().Be("/tool_calls/0/arguments/city");
        error.Message.Should().Contain("high surrogate U+D800");
    }

    [Test]
    public void UnicodeScalarLimits_RejectTheSecondScalarNotTheSecondUtf16CodeUnit()
    {
        var limits = ToolEnvelopeLimits.Constrained with { MaxFinalTextCharacters = 1 };
        var turn = TestCatalog.Turn(ToolChoice.None, limits: limits);

        turn.TryParse("{\"text\":\"😀😀\"}", out _, out var error).Should().BeFalse();

        error!.Code.Should().Be(ToolEnvelopeErrorCode.TextTooLarge);
        error.Message.Should().Contain("returned 2")
            .And.Contain("Unicode characters");
    }
}

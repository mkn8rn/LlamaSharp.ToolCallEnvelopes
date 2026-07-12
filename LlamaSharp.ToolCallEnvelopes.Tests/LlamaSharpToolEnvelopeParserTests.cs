using System.Text.Json;
using FluentAssertions;

namespace LlamaSharp.ToolCallEnvelopes.Tests;

[TestFixture]
public sealed class LlamaSharpToolEnvelopeParserTests
{
    [Test]
    public void Parse_MessageMode_ReturnsContent()
    {
        var result = LlamaSharpToolEnvelopeParser.Parse(
            """{"mode":"message","text":"Hello","calls":[]}""");

        result.Mode.Should().Be("message");
        result.Content.Should().Be("Hello");
        result.Refusal.Should().BeNull();
        result.ToolCalls.Should().BeEmpty();
    }

    [Test]
    public void Parse_RefusalMode_ReturnsRefusal()
    {
        var result = LlamaSharpToolEnvelopeParser.Parse(
            """{"mode":"refusal","text":"Not available","calls":[]}""");

        result.Mode.Should().Be("refusal");
        result.Content.Should().BeNull();
        result.Refusal.Should().Be("Not available");
        result.ToolCalls.Should().BeEmpty();
    }

    [Test]
    public void Parse_ToolCallsMode_ReturnsCalls()
    {
        var result = LlamaSharpToolEnvelopeParser.Parse("""
            {
              "mode": "tool_calls",
              "text": "",
              "calls": [
                { "id": "call_a", "name": "get_weather", "args": { "location": "London" } },
                { "id": "call_b", "name": "search", "args": { "query": "rain" } }
              ]
            }
            """);

        result.Mode.Should().Be("tool_calls");
        result.ToolCalls.Should().HaveCount(2);
        result.ToolCalls[0].Id.Should().Be("call_a");
        result.ToolCalls[0].Name.Should().Be("get_weather");
        using (var args = JsonDocument.Parse(result.ToolCalls[0].ArgumentsJson))
        {
            args.RootElement.GetProperty("location").GetString().Should().Be("London");
        }
        result.ToolCalls[1].Name.Should().Be("search");
    }

    [Test]
    public void Parse_InferredMessageWithoutMode_ReturnsContent()
    {
        var result = LlamaSharpToolEnvelopeParser.Parse("""{"text":"こんにちは"}""");

        result.Kind.Should().Be(ToolEnvelopeResultMode.Message);
        result.Content.Should().Be("こんにちは");
    }

    [Test]
    public void Parse_InferredToolCallsWithArgumentsProperty_ReturnsCalls()
    {
        var result = LlamaSharpToolEnvelopeParser.Parse("""
            {
              "tool_calls": [
                {
                  "id": "call_1",
                  "name": "get_weather",
                  "arguments": { "city": "Zagreb" }
                }
              ]
            }
            """);

        result.Kind.Should().Be(ToolEnvelopeResultMode.ToolCalls);
        using var arguments = JsonDocument.Parse(result.ToolCalls.Single().ArgumentsJson);
        arguments.RootElement.GetProperty("city").GetString().Should().Be("Zagreb");
    }

    [Test]
    public void Parse_InferredLegacyMessageModeWithCalls_UsesCallsAsSourceOfTruth()
    {
        var result = LlamaSharpToolEnvelopeParser.Parse("""
            {
              "mode": "message",
              "text": "The model mislabeled this call.",
              "calls": [{ "id": "call_1", "name": "lookup", "args": {} }]
            }
            """);

        result.Kind.Should().Be(ToolEnvelopeResultMode.ToolCalls);
        result.ToolCalls.Should().ContainSingle().Which.Name.Should().Be("lookup");
    }

    [Test]
    public void Parse_StrictDeclaredContradictionIncludesCodeAndPath()
    {
        var act = () => LlamaSharpToolEnvelopeParser.Parse(
            """{"mode":"message","text":"x","calls":[{"id":"c","name":"n","args":{}}]}""",
            new ToolEnvelopeParserOptions { EnvelopeMode = ToolEnvelopeMode.StrictDeclared });

        var error = act.Should().Throw<LlamaSharpToolEnvelopeException>().Which;
        error.Code.Should().Be("EnvelopeModePayloadMismatch");
        error.JsonPath.Should().Be("$.calls");
    }

    [Test]
    public void Parse_InvalidJson_ThrowsEnvelopeExceptionWithJsonInnerException()
    {
        var act = () => LlamaSharpToolEnvelopeParser.Parse("not json");

        act.Should().Throw<LlamaSharpToolEnvelopeException>()
            .WithInnerException<JsonException>();
    }

    [Test]
    public void Parse_EmptyString_ThrowsInsteadOfReturningEmptyMessage()
    {
        var act = () => LlamaSharpToolEnvelopeParser.Parse(string.Empty);

        act.Should().Throw<LlamaSharpToolEnvelopeException>()
            .WithMessage("*empty*");
    }

    [Test]
    public void Parse_MissingMode_Throws()
    {
        var act = () => LlamaSharpToolEnvelopeParser.Parse(
            """{"text":"fallback","calls":[]}""",
            new ToolEnvelopeParserOptions { EnvelopeMode = ToolEnvelopeMode.StrictDeclared });

        act.Should().Throw<LlamaSharpToolEnvelopeException>()
            .WithMessage("*missing required root property 'mode'*");
    }

    [Test]
    public void Parse_WrongTextType_Throws()
    {
        var act = () => LlamaSharpToolEnvelopeParser.Parse(
            """{"mode":"message","text":42,"calls":[]}""");

        act.Should().Throw<LlamaSharpToolEnvelopeException>()
            .WithMessage("*Property 'text' must be a string*");
    }

    [Test]
    public void Parse_MessageWithCalls_Throws()
    {
        var act = () => LlamaSharpToolEnvelopeParser.Parse(
            """{"mode":"message","text":"x","calls":[{"id":"c","name":"n","args":{}}]}""",
            new ToolEnvelopeParserOptions { EnvelopeMode = ToolEnvelopeMode.StrictDeclared });

        act.Should().Throw<LlamaSharpToolEnvelopeException>()
            .WithMessage("*Message envelopes must not contain tool calls*");
    }

    [Test]
    public void Parse_RefusalWithCalls_Throws()
    {
        var act = () => LlamaSharpToolEnvelopeParser.Parse(
            """{"mode":"refusal","text":"no","calls":[{"id":"c","name":"n","args":{}}]}""",
            new ToolEnvelopeParserOptions { EnvelopeMode = ToolEnvelopeMode.StrictDeclared });

        act.Should().Throw<LlamaSharpToolEnvelopeException>()
            .WithMessage("*Refusal envelopes must not contain tool calls*");
    }

    [Test]
    public void Parse_ToolCallsWithEmptyCalls_Throws()
    {
        var act = () => LlamaSharpToolEnvelopeParser.Parse(
            """{"mode":"tool_calls","text":"","calls":[]}""");

        act.Should().Throw<LlamaSharpToolEnvelopeException>()
            .WithMessage("*must contain at least one call*");
    }

    [Test]
    public void Parse_CallWithNoId_ThrowsInsteadOfGeneratingId()
    {
        var act = () => LlamaSharpToolEnvelopeParser.Parse(
            """{"mode":"tool_calls","text":"","calls":[{"id":"","name":"list_files","args":{}}]}""");

        act.Should().Throw<LlamaSharpToolEnvelopeException>()
            .WithMessage("*id must be a non-empty string*");
    }

    [TestCase("none")]
    [TestCase("null")]
    [TestCase("no_tool")]
    [TestCase("noop")]
    [TestCase("no-op")]
    [TestCase("n/a")]
    public void Parse_PseudoToolName_IsReturnedForCallerPolicyInsteadOfFiltered(string name)
    {
        var result = LlamaSharpToolEnvelopeParser.Parse(
            "{\"mode\":\"tool_calls\",\"text\":\"\",\"calls\":[{\"id\":\"call_1\",\"name\":\"" +
            name +
            "\",\"args\":{}}]}");

        result.ToolCalls.Should().ContainSingle()
            .Which.Name.Should().Be(name);
    }

    [Test]
    public void Parse_StringifiedArgs_ThrowsInsteadOfAcceptingRawStringToken()
    {
        var act = () => LlamaSharpToolEnvelopeParser.Parse("""
            {
              "mode": "tool_calls",
              "text": "",
              "calls": [
                { "id": "call_z", "name": "do_thing", "args": "{\"key\":\"value\"}" }
              ]
            }
            """);

        act.Should().Throw<LlamaSharpToolEnvelopeException>()
            .WithMessage("*Property 'args' must be a JSON object*");
    }

    [Test]
    public void Parse_ModeWithWrongCasing_Throws()
    {
        var act = () => LlamaSharpToolEnvelopeParser.Parse(
            """{"mode":"Tool_Calls","text":"","calls":[{"id":"c","name":"n","args":{}}]}""",
            new ToolEnvelopeParserOptions { EnvelopeMode = ToolEnvelopeMode.StrictDeclared });

        act.Should().Throw<LlamaSharpToolEnvelopeException>()
            .WithMessage("*Unsupported envelope mode*");
    }

    [Test]
    public void Parse_ExtraRootProperty_Throws()
    {
        var act = () => LlamaSharpToolEnvelopeParser.Parse(
            """{"mode":"message","text":"x","calls":[],"debug":true}""",
            new ToolEnvelopeParserOptions { EnvelopeMode = ToolEnvelopeMode.StrictDeclared });

        act.Should().Throw<LlamaSharpToolEnvelopeException>()
            .WithMessage("*unsupported root property 'debug'*");
    }
}

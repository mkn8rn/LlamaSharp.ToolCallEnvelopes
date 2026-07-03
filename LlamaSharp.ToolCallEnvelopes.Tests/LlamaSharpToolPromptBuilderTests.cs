using System.Text.Json;
using FluentAssertions;

namespace LlamaSharp.ToolCallEnvelopes.Tests;

[TestFixture]
public sealed class LlamaSharpToolPromptBuilderTests
{
    [Test]
    public void Build_NoTools_SystemPromptContainsEnvelopeContract()
    {
        var history = LlamaSharpToolPromptBuilder.Build(null, [], []);

        history.Messages.Should().ContainSingle();
        history.Messages[0].Role.Should().Be(ToolPromptRole.System);
        history.Messages[0].Content.Should().Contain("tool_calls");
        history.Messages[0].Content.Should().Contain("{\"mode\":\"message\"");
    }

    [Test]
    public void Build_WithSystemPrompt_PrependsSystemPrompt()
    {
        var history = LlamaSharpToolPromptBuilder.Build("Be concise.", [], []);

        history.Messages[0].Content.Should().StartWith("Be concise.");
        history.Messages[0].Content.Should().Contain("## Tool calling");
    }

    [Test]
    public void Build_WithTools_ListsToolAndParameterDetailsWhenNotStrict()
    {
        var tools = new[]
        {
            Tool("get_weather", "Gets weather.", """
                {
                  "type": "object",
                  "properties": {
                    "location": { "type": "string", "description": "City name" }
                  }
                }
                """)
        };

        var history = LlamaSharpToolPromptBuilder.Build(null, [], tools, strictTools: false);

        history.Messages[0].Content.Should().Contain("get_weather");
        history.Messages[0].Content.Should().Contain("Gets weather.");
        history.Messages[0].Content.Should().Contain("location");
        history.Messages[0].Content.Should().Contain("City name");
    }

    [Test]
    public void Build_WithStrictTools_OmitsParameterBullets()
    {
        var tools = new[]
        {
            Tool("get_weather", "Gets weather.", """
                {
                  "type": "object",
                  "properties": {
                    "location": { "type": "string", "description": "City name" }
                  }
                }
                """)
        };

        var history = LlamaSharpToolPromptBuilder.Build(null, [], tools, strictTools: true);

        history.Messages[0].Content.Should().Contain("get_weather");
        history.Messages[0].Content.Should().NotContain("Parameters:");
        history.Messages[0].Content.Should().NotContain("City name");
    }

    [Test]
    public void Build_WithRefusal_IncludesRefusalShape()
    {
        var history = LlamaSharpToolPromptBuilder.Build(null, [], [], allowRefusal: true);

        history.Messages[0].Content.Should().Contain("{\"mode\":\"refusal\"");
        history.Messages[0].Content.Should().Contain("\"refusal\"");
    }

    [Test]
    public void Build_WithImageCount_AppendsVisualContext()
    {
        var history = LlamaSharpToolPromptBuilder.Build("sys", [], [], imageCount: 2);

        history.Messages[0].Content.Should().Contain("Visual context");
        history.Messages[0].Content.Should().Contain("shown 2 images");
    }

    [Test]
    public void Build_PlainAssistantMessage_WrapsAsMessageEnvelope()
    {
        var history = LlamaSharpToolPromptBuilder.Build(
            null,
            [ToolAwareMessage.Assistant("Sure.")],
            []);

        var assistant = history.Messages.Single(m => m.Role == ToolPromptRole.Assistant);
        using var document = JsonDocument.Parse(assistant.Content);
        document.RootElement.GetProperty("mode").GetString().Should().Be("message");
        document.RootElement.GetProperty("text").GetString().Should().Be("Sure.");
        document.RootElement.GetProperty("calls").GetArrayLength().Should().Be(0);
    }

    [Test]
    public void Build_AssistantWithToolCalls_WrapsAsToolCallsEnvelope()
    {
        var history = LlamaSharpToolPromptBuilder.Build(
            null,
            [ToolAwareMessage.AssistantWithToolCalls(
                [new ToolCall("call_1", "get_weather", """{"location":"Paris"}""")])],
            []);

        var assistant = history.Messages.Single(m => m.Role == ToolPromptRole.Assistant);
        using var document = JsonDocument.Parse(assistant.Content);
        document.RootElement.GetProperty("mode").GetString().Should().Be("tool_calls");
        var call = document.RootElement.GetProperty("calls")[0];
        call.GetProperty("id").GetString().Should().Be("call_1");
        call.GetProperty("name").GetString().Should().Be("get_weather");
        call.GetProperty("args").GetProperty("location").GetString().Should().Be("Paris");
    }

    [Test]
    public void Build_AssistantWithInvalidArgs_ThrowsInsteadOfUsingEmptyObject()
    {
        var act = () => LlamaSharpToolPromptBuilder.Build(
            null,
            [ToolAwareMessage.AssistantWithToolCalls(
                [new ToolCall("call_1", "broken", "not-json")])],
            []);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*valid JSON object*");
    }

    [Test]
    public void Build_AssistantWithEmptyArgs_ThrowsInsteadOfUsingEmptyObject()
    {
        var act = () => LlamaSharpToolPromptBuilder.Build(
            null,
            [ToolAwareMessage.AssistantWithToolCalls(
                [new ToolCall("call_1", "empty", string.Empty)])],
            []);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*JSON object*");
    }

    [Test]
    public void Build_ToolResult_FormatsAsUserToolResult()
    {
        var history = LlamaSharpToolPromptBuilder.Build(
            null,
            [ToolAwareMessage.ToolResult("call_1", "Sunny")],
            []);

        var resultMessage = history.Messages.Single(m => m.Role == ToolPromptRole.User);
        using var document = JsonDocument.Parse(resultMessage.Content);
        var result = document.RootElement.GetProperty("tool_result");
        result.GetProperty("id").GetString().Should().Be("call_1");
        result.GetProperty("content").GetString().Should().Be("Sunny");
    }

    [Test]
    public void Build_ToolResultWithImage_AddsImageMarker()
    {
        var history = LlamaSharpToolPromptBuilder.Build(
            null,
            [ToolAwareMessage.ToolResultWithImage("call_1", "OCR text", "abc")],
            []);

        var resultMessage = history.Messages.Single(m => m.Role == ToolPromptRole.User);
        resultMessage.Content.Should().Contain("[Image attached]\\nOCR text");
    }

    [Test]
    public void Build_MultiTurn_PreservesRoleOrder()
    {
        var history = LlamaSharpToolPromptBuilder.Build(
            "sys",
            [
                ToolAwareMessage.User("Question"),
                ToolAwareMessage.AssistantWithToolCalls([new ToolCall("c1", "lookup", "{}")]),
                ToolAwareMessage.ToolResult("c1", "Result"),
                ToolAwareMessage.Assistant("Answer"),
            ],
            []);

        history.Messages.Select(m => m.Role).Should().Equal(
            ToolPromptRole.System,
            ToolPromptRole.User,
            ToolPromptRole.Assistant,
            ToolPromptRole.User,
            ToolPromptRole.Assistant);
    }

    [Test]
    public void Build_UnknownRole_ThrowsInsteadOfMappingToUser()
    {
        var act = () => LlamaSharpToolPromptBuilder.Build(
            null,
            [new ToolAwareMessage { Role = "developer", Content = "internal note" }],
            []);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Unsupported tool-aware message role*");
    }

    private static ToolDefinition Tool(string name, string description, string schemaJson)
    {
        using var document = JsonDocument.Parse(schemaJson);
        return new ToolDefinition(name, description, document.RootElement.Clone());
    }
}

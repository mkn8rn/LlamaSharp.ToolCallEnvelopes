using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace LlamaSharp.ToolCallEnvelopes.Tests;

[TestFixture]
public sealed class LlamaSharpToolGrammarTests
{
    private static readonly Regex BrokenContinuationLine =
        new(@"^\s+\|", RegexOptions.Multiline | RegexOptions.Compiled);

    [Test]
    public void Build_DefaultGrammar_ContainsEnvelopeRules()
    {
        var grammar = LlamaSharpToolGrammar.Build();

        grammar.Should().Contain("root");
        grammar.Should().Contain("mode-val");
        grammar.Should().Contain("calls-arr");
        grammar.Should().Contain("call-obj");
        grammar.Should().Contain("\\\"message\\\"");
        grammar.Should().Contain("\\\"tool_calls\\\"");
    }

    [Test]
    public void Build_DefaultGrammar_IsCached()
    {
        var first = LlamaSharpToolGrammar.Build();
        var second = LlamaSharpToolGrammar.Build();

        ReferenceEquals(first, second).Should().BeTrue();
    }

    [Test]
    public void Build_ToolChoiceNone_OmitsToolCallMode()
    {
        var grammar = LlamaSharpToolGrammar.Build(ToolChoice.None);

        grammar.Should().Contain("\\\"message\\\"");
        grammar.Should().NotContain("\\\"tool_calls\\\"");
        grammar.Should().Contain("calls-arr ::= \"[\" ws \"]\"");
    }

    [Test]
    public void Build_ToolChoiceRequired_OmitsMessageMode()
    {
        var grammar = LlamaSharpToolGrammar.Build(ToolChoice.Required);

        grammar.Should().NotContain("\\\"message\\\"");
        grammar.Should().Contain("\\\"tool_calls\\\"");
    }

    [Test]
    public void Build_ToolChoiceNamed_PinsFunctionNameAndSingleCall()
    {
        var grammar = LlamaSharpToolGrammar.Build(ToolChoice.ForFunction("get_weather"));

        grammar.Should().Contain("\\\"get_weather\\\"");
        grammar.Should().NotContain("ws \",\" ws call-obj");
    }

    [Test]
    public void Build_ToolChoiceNamedWithInvalidName_Throws()
    {
        var act = () => LlamaSharpToolGrammar.Build(ToolChoice.ForFunction("bad name"));

        act.Should().Throw<ArgumentException>();
    }

    [TestCase(true, true)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(false, false)]
    public void Build_AllAutoCombinations_HaveParserFriendlyLineShape(
        bool parallelCalls,
        bool allowRefusal)
    {
        var grammar = LlamaSharpToolGrammar.Build(
            ToolChoice.Auto,
            parallelCalls,
            allowRefusal);

        BrokenContinuationLine.IsMatch(grammar).Should().BeFalse();
    }

    [Test]
    public void Build_StrictToolSchema_PinsNameAndArgsRule()
    {
        var tools = new[]
        {
            Tool("get_weather", """
                {
                  "type": "object",
                  "properties": {
                    "location": { "type": "string" },
                    "units": { "type": "string", "enum": ["celsius", "fahrenheit"] }
                  },
                  "required": ["location"],
                  "additionalProperties": false
                }
                """)
        };

        var grammar = LlamaSharpToolGrammar.Build(
            ToolChoice.Auto,
            parallelCalls: true,
            tools,
            strict: true);

        grammar.Should().Contain("call-obj-0");
        grammar.Should().Contain("\\\"get_weather\\\"");
        grammar.Should().Contain("t0-top");
        grammar.Should().Contain("\\\"celsius\\\"");
        grammar.Should().NotContain("""\"args\"" ws ":" ws obj""");
    }

    [Test]
    public void Build_StrictNamedTool_EmitsOnlyNamedBranch()
    {
        var tools = new[]
        {
            Tool("first", """{"type":"object","properties":{"x":{"type":"integer"}}}"""),
            Tool("second", """{"type":"object","properties":{"y":{"type":"string"}}}"""),
        };

        var grammar = LlamaSharpToolGrammar.Build(
            ToolChoice.ForFunction("second"),
            parallelCalls: true,
            tools,
            strict: true);

        grammar.Should().Contain("\\\"second\\\"");
        grammar.Should().NotContain("\\\"first\\\"");
        grammar.Should().NotContain("ws \",\" ws call-obj");
    }

    [Test]
    public void Build_StrictNamedToolNotInList_Throws()
    {
        var tools = new[]
        {
            Tool("other", """{"type":"object","properties":{"x":{"type":"integer"}}}"""),
        };

        var act = () => LlamaSharpToolGrammar.Build(
            ToolChoice.ForFunction("missing"),
            parallelCalls: true,
            tools,
            strict: true);

        act.Should().Throw<LlamaSharpToolSchemaException>();
    }

    [Test]
    public void Build_StrictUnsupportedSchema_ThrowsInsteadOfFallingBackToObj()
    {
        var tools = new[]
        {
            Tool("choose", """
                {
                  "type": "object",
                  "properties": {
                    "value": { "oneOf": [{ "type": "string" }, { "type": "integer" }] }
                  }
                }
                """)
        };

        var act = () => LlamaSharpToolGrammar.Build(
            ToolChoice.Auto,
            parallelCalls: true,
            tools,
            strict: true);

        act.Should().Throw<LlamaSharpToolSchemaException>()
            .Which.UnsupportedKeywords.Should().Contain(u => u.Contains("oneOf", StringComparison.Ordinal));
    }

    [Test]
    public void JsonSchemaConverter_FragmentReportsUnsupportedKeywords()
    {
        using var document = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "value": { "not": { "type": "null" } }
              }
            }
            """);

        var ok = LlamaSharpJsonSchemaConverter.TryConvertFragment(
            document.RootElement,
            "t0-",
            out var topRule,
            out var body,
            out var unsupported);

        ok.Should().BeTrue();
        topRule.Should().NotBeNullOrWhiteSpace();
        body.Should().NotBeNullOrWhiteSpace();
        unsupported.Should().Contain(u => u.Contains("/not", StringComparison.Ordinal));
    }

    private static ToolDefinition Tool(string name, string schemaJson)
    {
        using var document = JsonDocument.Parse(schemaJson);
        return new ToolDefinition(name, $"Tool {name}", document.RootElement.Clone());
    }
}

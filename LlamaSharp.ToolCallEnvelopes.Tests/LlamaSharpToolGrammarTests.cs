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
    public void Build_StrictMissingRootType_NormalizesToolArgumentsToObject()
    {
        var grammar = LlamaSharpToolGrammar.BuildCompleteEnvelopeGrammar(
            [Tool("lookup", """{"properties":{"query":{"type":"string"}},"required":["query"]}""")],
            new ToolEnvelopeGrammarOptions
            {
                ToolChoice = ToolChoice.Required,
                EnvelopeMode = ToolEnvelopeMode.Inferred,
                StrictTools = true,
            });

        grammar.Should().Contain("t0-top-obj");
        grammar.Should().Contain("\\\"query\\\"");
        grammar.Should().NotContain("\"\\\"arguments\\\"\" ws \":\" ws value");
    }

    [Test]
    public void Build_StrictEmptySchema_NormalizesToolArgumentsToEmptyObject()
    {
        var grammar = LlamaSharpToolGrammar.BuildCompleteEnvelopeGrammar(
            [Tool("empty", "{}")],
            new ToolEnvelopeGrammarOptions
            {
                ToolChoice = ToolChoice.Required,
                StrictTools = true,
            });

        grammar.Should().Contain("\"\\\"arguments\\\"\" ws \":\" ws object");
        grammar.Should().Contain("object ::= obj");
    }

    [Test]
    public void Build_StrictExplicitNonObjectRoot_ThrowsBeforeGeneration()
    {
        var act = () => LlamaSharpToolGrammar.BuildCompleteEnvelopeGrammar(
            [Tool("invalid", """{"type":"string"}""")],
            new ToolEnvelopeGrammarOptions
            {
                ToolChoice = ToolChoice.Required,
                StrictTools = true,
            });

        act.Should().Throw<LlamaSharpToolSchemaException>()
            .Which.UnsupportedKeywords.Should().ContainSingle()
            .Which.Should().Contain("object root");
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

    [TestCase(ToolEnvelopeMode.Inferred, true)]
    [TestCase(ToolEnvelopeMode.Inferred, false)]
    [TestCase(ToolEnvelopeMode.StrictDeclared, true)]
    [TestCase(ToolEnvelopeMode.StrictDeclared, false)]
    public void BuildComplete_AutoWithEmptyCatalog_OmitsToolCallAlternative(
        ToolEnvelopeMode envelopeMode,
        bool strictTools)
    {
        var grammar = LlamaSharpToolGrammar.BuildCompleteEnvelopeGrammar(
            [],
            new ToolEnvelopeGrammarOptions
            {
                ToolChoice = ToolChoice.Auto,
                EnvelopeMode = envelopeMode,
                StrictTools = strictTools,
            });

        grammar.Split('\n')[0].Should().Be(envelopeMode == ToolEnvelopeMode.Inferred
            ? "root ::= inferred-message-envelope"
            : "root ::= message-envelope");
        grammar.Should().NotContain("tool-calls-envelope");
    }

    [TestCase(ToolEnvelopeMode.Inferred)]
    [TestCase(ToolEnvelopeMode.StrictDeclared)]
    public void BuildComplete_AutoEmptyCatalogCanStillAllowRefusal(
        ToolEnvelopeMode envelopeMode)
    {
        var grammar = LlamaSharpToolGrammar.BuildCompleteEnvelopeGrammar(
            [],
            new ToolEnvelopeGrammarOptions
            {
                ToolChoice = ToolChoice.Auto,
                EnvelopeMode = envelopeMode,
                AllowRefusal = true,
            });

        grammar.Split('\n')[0].Should().Contain("refusal-envelope");
        grammar.Should().NotContain("tool-calls-envelope");
    }

    [TestCase(ToolEnvelopeMode.Inferred, true)]
    [TestCase(ToolEnvelopeMode.Inferred, false)]
    [TestCase(ToolEnvelopeMode.StrictDeclared, true)]
    [TestCase(ToolEnvelopeMode.StrictDeclared, false)]
    public void BuildComplete_RequiredWithEmptyCatalog_ThrowsBeforeGeneration(
        ToolEnvelopeMode envelopeMode,
        bool strictTools)
    {
        var act = () => LlamaSharpToolGrammar.BuildCompleteEnvelopeGrammar(
            [],
            new ToolEnvelopeGrammarOptions
            {
                ToolChoice = ToolChoice.Required,
                EnvelopeMode = envelopeMode,
                StrictTools = strictTools,
            });

        act.Should().Throw<ArgumentException>()
            .WithMessage("*requires at least one supplied tool*");
    }

    [TestCase(ToolEnvelopeMode.Inferred, true)]
    [TestCase(ToolEnvelopeMode.Inferred, false)]
    [TestCase(ToolEnvelopeMode.StrictDeclared, true)]
    [TestCase(ToolEnvelopeMode.StrictDeclared, false)]
    public void BuildComplete_NoneWithEmptyCatalog_RemainsMessageOnly(
        ToolEnvelopeMode envelopeMode,
        bool strictTools)
    {
        var grammar = LlamaSharpToolGrammar.BuildCompleteEnvelopeGrammar(
            [],
            new ToolEnvelopeGrammarOptions
            {
                ToolChoice = ToolChoice.None,
                EnvelopeMode = envelopeMode,
                StrictTools = strictTools,
            });

        grammar.Split('\n')[0].Should().Be(envelopeMode == ToolEnvelopeMode.Inferred
            ? "root ::= inferred-message-envelope"
            : "root ::= message-envelope");
        grammar.Should().NotContain("tool-calls-envelope");
    }

    [TestCase(ToolEnvelopeMode.Inferred, true)]
    [TestCase(ToolEnvelopeMode.Inferred, false)]
    [TestCase(ToolEnvelopeMode.StrictDeclared, true)]
    [TestCase(ToolEnvelopeMode.StrictDeclared, false)]
    public void BuildComplete_NamedWithEmptyCatalog_ThrowsBeforeGeneration(
        ToolEnvelopeMode envelopeMode,
        bool strictTools)
    {
        var act = () => LlamaSharpToolGrammar.BuildCompleteEnvelopeGrammar(
            [],
            new ToolEnvelopeGrammarOptions
            {
                ToolChoice = ToolChoice.ForFunction("missing"),
                EnvelopeMode = envelopeMode,
                StrictTools = strictTools,
            });

        act.Should().Throw<LlamaSharpToolSchemaException>()
            .Which.UnsupportedKeywords.Should().ContainSingle()
            .Which.Should().Contain("did not match");
    }

    [TestCase(true)]
    [TestCase(false)]
    public void BuildComplete_NamedChoiceMustExistInAuthoritativeCatalog(bool strictTools)
    {
        var act = () => LlamaSharpToolGrammar.BuildCompleteEnvelopeGrammar(
            [Tool("other", """{"type":"object"}""")],
            new ToolEnvelopeGrammarOptions
            {
                ToolChoice = ToolChoice.ForFunction("missing"),
                StrictTools = strictTools,
            });

        act.Should().Throw<LlamaSharpToolSchemaException>()
            .Which.UnsupportedKeywords.Should().ContainSingle()
            .Which.Should().Contain("did not match");
    }

    [Test]
    public void BuildComplete_NonStrictCatalogStillPinsKnownToolNames()
    {
        var grammar = LlamaSharpToolGrammar.BuildCompleteEnvelopeGrammar(
            [
                Tool("first", """{"type":"object"}"""),
                Tool("second", """{"type":"object"}"""),
            ],
            new ToolEnvelopeGrammarOptions
            {
                ToolChoice = ToolChoice.Auto,
                EnvelopeMode = ToolEnvelopeMode.Inferred,
                StrictTools = false,
            });

        grammar.Should().Contain("\"\\\"first\\\"\"");
        grammar.Should().Contain("\"\\\"second\\\"\"");
        grammar.Should().NotContain("\"\\\"name\\\"\" ws \":\" ws string");
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

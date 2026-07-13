using LLama.Sampling;

namespace LlamaSharp.ToolCallEnvelopes.Tests;

[TestFixture]
public sealed class GrammarNativeTests
{
    public static IEnumerable<TestCaseData> NativeMatrix
    {
        get
        {
            foreach (var allowRefusal in new[] { false, true })
                foreach (var maxCalls in new[] { 1, 3 })
                {
                    yield return Case([], ToolChoice.Auto, allowRefusal, maxCalls, "empty-auto");
                    yield return Case([], ToolChoice.None, allowRefusal, maxCalls, "empty-none");
                    yield return Case([TestCatalog.Weather()], ToolChoice.Auto, allowRefusal, maxCalls, "one-auto");
                    yield return Case([TestCatalog.Weather()], ToolChoice.None, allowRefusal, maxCalls, "one-none");
                    yield return Case([TestCatalog.Weather()], ToolChoice.Required, allowRefusal, maxCalls, "one-required");
                    yield return Case(
                        [TestCatalog.Weather(), TestCatalog.Search()],
                        ToolChoice.Named("search"),
                        allowRefusal,
                        maxCalls,
                        "two-named");
                }
        }
    }

    [TestCaseSource(nameof(NativeMatrix))]
    public void EveryLegalChoiceVariant_CreatesTheLlamaSharpAdapterValue(
        ToolDefinition[] tools,
        ToolChoice choice,
        bool allowRefusal,
        int maxCalls)
    {
        var plan = TestCatalog.Plan(
            allowRefusal: allowRefusal,
            maxCalls: maxCalls,
            tools: tools);
        var turn = plan.CreateTurn("Policy.", [], choice);

        var grammar = new Grammar(turn.Grammar, "root");

        grammar.Gbnf.Should().Be(turn.Grammar);
        grammar.Root.Should().Be("root");
    }

    [Test]
    public void Grammar_HasOneDescriptiveWireContractAndNoAliases()
    {
        var turn = TestCatalog.Turn(allowRefusal: true, maxCalls: 2);

        turn.Grammar.Should().StartWith("root ::= assistant-message | tool-request | refusal\n");
        turn.Grammar.Should().Contain("\"\\\"text\\\"\"")
            .And.Contain("\"\\\"tool_calls\\\"\"")
            .And.Contain("\"\\\"arguments\\\"\"")
            .And.Contain("\"\\\"refusal\\\"\"")
            .And.NotContain("\"\\\"mode\\\"\"")
            .And.NotContain("\"\\\"calls\\\"\"")
            .And.NotContain("\"\\\"args\\\"\"")
            .And.NotContain("\"\\\"id\\\"\"");
        turn.Grammar.Should().Contain("gap ::= [ \\t]?")
            .And.NotContain("gap ::= [ \\t\\n\\r]*");
    }

    [Test]
    public void Grammar_BoundsCallArraysAndEveryGenericRepetition()
    {
        var turn = TestCatalog.Turn(maxCalls: 3);

        turn.Grammar.Should().Contain("additional-tool-call-up-to-2")
            .And.Contain("additional-tool-call-exact-2");
        turn.Grammar.Should().NotContain("tool-call )*")
            .And.NotContain("json-char*")
            .And.NotContain("[0-9]*")
            .And.NotContain("[0-9]+")
            .And.NotMatchRegex("\\{[0-9]{4,}(?:,[0-9]*)?\\}");
    }

    [Test]
    public void NumericGrammar_UsesOnlyBoundedDecimalForms()
    {
        var limits = ToolEnvelopeLimits.Constrained with
        {
            MaxGeneratedNumberCharacters = 12,
        };
        var turn = TestCatalog.Turn(ToolChoice.Required, limits: limits);
        var integer = turn.Grammar.Split('\n')
            .Single(line => line.StartsWith("integer-value ::=", StringComparison.Ordinal));
        var number = turn.Grammar.Split('\n')
            .Single(line => line.StartsWith("number-value ::=", StringComparison.Ordinal));

        integer.Should().Contain("positive-integer-digit-up-to-11")
            .And.Contain("negative-integer-digit-up-to-10");
        number.Should().Contain("integer-value")
            .And.NotContain("[eE]")
            .And.NotContain("{1,60}");
        var grammar = new Grammar(turn.Grammar, "root");
        grammar.Gbnf.Should().Be(turn.Grammar);
    }

    [Test]
    public void Grammar_EmitsRequiredPropertiesFirstAndEachOptionalPropertyOnce()
    {
        var turn = TestCatalog.Turn(ToolChoice.Required);
        var objectRule = turn.Grammar.Split('\n')
            .Single(line => line.StartsWith("t0-schema-0 ::=", StringComparison.Ordinal));

        objectRule.IndexOf("city", StringComparison.Ordinal)
            .Should().BeLessThan(objectRule.IndexOf("unit", StringComparison.Ordinal));
        Count(objectRule, "days").Should().Be(1);
        Count(objectRule, "include_alerts").Should().Be(1);
        Count(objectRule, "tags").Should().Be(1);
        Count(objectRule, "coordinates").Should().Be(1);
    }

    [Test]
    public void NamedGrammar_ContainsOnlyTheSelectedToolTerminalAndSchema()
    {
        var turn = TestCatalog.Turn(
            ToolChoice.Named("search"),
            tools: [TestCatalog.Weather(), TestCatalog.Search()]);

        turn.Grammar.Should().Contain("search")
            .And.NotContain("get_weather")
            .And.Contain("t1-schema-")
            .And.NotContain("t0-schema-");
    }

    [Test]
    public void Grammar_IsDeterministicAcrossEquivalentPlans()
    {
        var first = TestCatalog.Turn(ToolChoice.Required);
        var second = TestCatalog.Turn(ToolChoice.Required);

        first.Grammar.Should().Be(second.Grammar);
        first.GrammarCacheKey.Should().Be(second.GrammarCacheKey);
    }

    private static TestCaseData Case(
        ToolDefinition[] tools,
        ToolChoice choice,
        bool allowRefusal,
        int maxCalls,
        string name) =>
        new TestCaseData(tools, choice, allowRefusal, maxCalls)
            .SetName($"Native_{name}_refusal_{allowRefusal}_calls_{maxCalls}");

    private static int Count(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}

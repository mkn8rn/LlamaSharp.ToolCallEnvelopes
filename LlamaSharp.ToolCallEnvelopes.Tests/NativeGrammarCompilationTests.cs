using LLama;
using LLama.Common;
using LLama.Native;

namespace LlamaSharp.ToolCallEnvelopes.Tests;

[TestFixture]
[Category("NativeModel")]
public sealed class NativeGrammarCompilationTests
{
    private LLamaWeights _weights = null!;

    public static IEnumerable<TestCaseData> EveryLegalPolicyGrammar
    {
        get
        {
            foreach (var toolCount in new[] { 0, 1, 2 })
                foreach (var allowRefusal in new[] { false, true })
                    foreach (var maxCalls in new[] { 1, 3 })
                        foreach (var choice in LegalChoices(toolCount))
                        {
                            yield return new TestCaseData(toolCount, allowRefusal, maxCalls, choice)
                                .SetName(
                                    $"Native_compile_tools_{toolCount}_refusal_{allowRefusal}_calls_{maxCalls}_"
                                    + choice.ToString().Replace('(', '_').Replace(")", string.Empty));
                        }
        }
    }

    [OneTimeSetUp]
    public void LoadVocabulary()
    {
        var path = Environment.GetEnvironmentVariable("LLAMASHARP_TEST_MODEL");
        if (string.IsNullOrWhiteSpace(path))
        {
            Assert.Ignore(
                "Native grammar compilation requires LLAMASHARP_TEST_MODEL to name a local GGUF "
                + "model. Release validation must run this category with a real model vocabulary.");
        }

        if (!File.Exists(path))
        {
            Assert.Fail(
                $"LLAMASHARP_TEST_MODEL points to '{path}', but that GGUF file does not exist. "
                + "Correct the path before native release validation.");
        }

        _weights = LLamaWeights.LoadFromFile(new ModelParams(path)
        {
            GpuLayerCount = 0,
            VocabOnly = true,
        });
    }

    [OneTimeTearDown]
    public void DisposeVocabulary() => _weights?.Dispose();

    [TestCaseSource(nameof(EveryLegalPolicyGrammar))]
    public void EveryLegalPolicyGrammar_CompilesInLlamaCppAgainstARealVocabulary(
        int toolCount,
        bool allowRefusal,
        int maxCalls,
        ToolChoice choice)
    {
        var plan = TestCatalog.Plan(
            allowRefusal: allowRefusal,
            maxCalls: maxCalls,
            tools: Tools(toolCount));
        var turn = plan.CreateTurn("Policy.", [], choice);
        using var chain = SafeLLamaSamplerChainHandle.Create(LLamaSamplerChainParams.Default());

        chain.AddGrammar(_weights.Vocab, turn.Grammar, "root");

        chain.Count.Should().Be(1);
        chain.GetName(0).Should().Contain("grammar");
    }

    [Test]
    public void LargeConfiguredBounds_CompileWithoutNativeRepetitionExpansion()
    {
        const string schema =
            """
            {
              "type":"object",
              "properties":{
                "text":{"type":"string"},
                "items":{"type":"array","items":{"type":"integer"}},
                "score":{"type":"number"}
              },
              "required":["text","items","score"],
              "additionalProperties":false
            }
            """;
        var limits = ToolEnvelopeLimits.Constrained with
        {
            MaxEnvelopeCharacters = 100_000,
            MaxFinalTextCharacters = 8_192,
            MaxRefusalCharacters = 4_096,
            MaxGeneratedStringCharacters = 8_192,
            MaxGeneratedArrayItems = 4_096,
            MaxGeneratedNumberCharacters = 4_096,
        };
        var plan = TestCatalog.Plan(
            allowRefusal: true,
            maxCalls: 4_096,
            limits: limits,
            tools:
            [
                ToolDefinition.Parse(
                    "large_bounds",
                    "Exercises large native grammar bounds.",
                    schema),
            ]);

        foreach (var choice in new[]
                 {
                     ToolChoice.Auto,
                     ToolChoice.None,
                     ToolChoice.Required,
                     ToolChoice.Named("large_bounds"),
                 })
        {
            var turn = plan.CreateTurn("Policy.", [], choice);
            using var chain = SafeLLamaSamplerChainHandle.Create(
                LLamaSamplerChainParams.Default());

            chain.AddGrammar(_weights.Vocab, turn.Grammar, "root");

            chain.Count.Should().Be(1, choice.ToString());
            chain.GetName(0).Should().Contain("grammar", choice.ToString());
            turn.Grammar.Should().NotMatchRegex(
                "\\{[0-9]{4,}(?:,[0-9]*)?\\}",
                choice.ToString());
        }
    }

    [Test]
    public void NativePolicyMatrix_CoversEveryLegalCombinationExactlyOnce()
    {
        var cases = EveryLegalPolicyGrammar.ToArray();

        cases.Should().HaveCount(44);
        cases.Select(test => test.TestName).Should().OnlyHaveUniqueItems();
    }

    private static ToolDefinition[] Tools(int count) => count switch
    {
        0 => [],
        1 => [TestCatalog.Weather()],
        2 => [TestCatalog.Weather(), TestCatalog.Search()],
        _ => throw new ArgumentOutOfRangeException(nameof(count)),
    };

    private static IEnumerable<ToolChoice> LegalChoices(int toolCount)
    {
        yield return ToolChoice.Auto;
        yield return ToolChoice.None;
        if (toolCount == 0)
            yield break;

        yield return ToolChoice.Required;
        yield return ToolChoice.Named("get_weather");
        if (toolCount == 2)
            yield return ToolChoice.Named("search");
    }
}

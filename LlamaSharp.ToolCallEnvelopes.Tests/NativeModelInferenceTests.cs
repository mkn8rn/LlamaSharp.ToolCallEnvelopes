using System.Text;
using LLama;
using LLama.Common;
using LLama.Sampling;

namespace LlamaSharp.ToolCallEnvelopes.Tests;

[TestFixture]
[Category("NativeModel")]
public sealed class NativeModelInferenceTests
{
    private LLamaWeights _weights = null!;
    private ModelParams _modelParameters = null!;

    public static IEnumerable<TestCaseData> EveryPolicyAndRefusalSetting
    {
        get
        {
            foreach (var allowRefusal in new[] { false, true })
            {
                yield return Case(ToolChoice.Auto, allowRefusal);
                yield return Case(ToolChoice.None, allowRefusal);
                yield return Case(ToolChoice.Required, allowRefusal);
                yield return Case(ToolChoice.Named("echo"), allowRefusal);
            }
        }
    }

    [OneTimeSetUp]
    public void LoadModel()
    {
        var path = Environment.GetEnvironmentVariable("LLAMASHARP_TEST_MODEL");
        if (string.IsNullOrWhiteSpace(path))
        {
            Assert.Ignore(
                "Native model inference requires LLAMASHARP_TEST_MODEL to name a local GGUF "
                + "model. Release validation must run this category with a real model.");
        }

        if (!File.Exists(path))
        {
            Assert.Fail(
                $"LLAMASHARP_TEST_MODEL points to '{path}', but that GGUF file does not exist. "
                + "Correct the path before native release validation.");
        }

        _modelParameters = new ModelParams(path)
        {
            ContextSize = 1_024,
            BatchSize = 128,
            GpuLayerCount = 0,
            Threads = Math.Max(1, Environment.ProcessorCount / 2),
        };
        _weights = LLamaWeights.LoadFromFile(_modelParameters);
    }

    [OneTimeTearDown]
    public void DisposeModel() => _weights?.Dispose();

    [TestCaseSource(nameof(EveryPolicyAndRefusalSetting))]
    [CancelAfter(30_000)]
    public async Task EveryPolicyAndRefusalSetting_GeneratesAnEnvelopeAcceptedByItsTurn(
        ToolChoice choice,
        bool allowRefusal)
    {
        var limits = ToolEnvelopeLimits.Constrained with
        {
            MaxEnvelopeCharacters = 512,
            MaxFinalTextCharacters = 64,
            MaxRefusalCharacters = 64,
            MaxGeneratedStringCharacters = 32,
        };
        var plan = TestCatalog.Plan(
            allowRefusal: allowRefusal,
            limits: limits,
            tools: [EchoTool(), LookupTool()]);
        var turn = plan.CreateTurn(
            "Return the shortest useful response allowed by OUTPUT_CONTRACT.",
            [ToolMessage.User(RequestFor(choice))],
            choice);
        var output = await InferAsync(turn, TestContext.CurrentContext.CancellationToken);

        var outcome = turn.Parse(output);
        switch (choice.Kind)
        {
            case ToolChoiceKind.None:
                outcome.Should().BeAssignableTo<ToolEnvelopeOutcome.Final>();
                break;
            case ToolChoiceKind.Required:
                outcome.Should().BeOfType<ToolEnvelopeOutcome.ToolRequest>();
                break;
            case ToolChoiceKind.Named:
                outcome.Should().BeOfType<ToolEnvelopeOutcome.ToolRequest>()
                    .Which.Calls.Should().OnlyContain(call => call.Name == choice.ToolName);
                break;
            case ToolChoiceKind.Auto:
                outcome.Should().BeAssignableTo<ToolEnvelopeOutcome>();
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(choice),
                    choice,
                    "The native policy matrix has no assertion for this ToolChoiceKind. Add the "
                    + "new policy's legal outcome before running release validation.");
        }
    }

    [Test]
    public void NativeInferenceMatrix_CoversEveryPolicyAndRefusalCombinationExactlyOnce()
    {
        var cases = EveryPolicyAndRefusalSetting.ToArray();

        cases.Should().HaveCount(8);
        cases.Select(test => test.TestName).Should().OnlyHaveUniqueItems();
    }

    private async Task<string> InferAsync(
        ToolEnvelopeTurn turn,
        CancellationToken cancellationToken)
    {
        using var context = _weights.CreateContext(_modelParameters);
        var executor = new InteractiveExecutor(context);
        var inference = new InferenceParams
        {
            MaxTokens = 128,
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Grammar = new Grammar(turn.Grammar, "root"),
                Temperature = 0.1f,
                TopP = 0.9f,
                Seed = 42,
            },
        };
        var output = new StringBuilder();
        await foreach (var fragment in executor.InferAsync(
                           ApplyNativeTemplate(turn.Prompt),
                           inference,
                           cancellationToken))
        {
            output.Append(fragment);
        }

        return output.ToString();
    }

    private string ApplyNativeTemplate(IReadOnlyList<ToolMessage> messages)
    {
        var template = new LLamaTemplate(_weights, strict: true)
        {
            AddAssistant = true,
        };
        foreach (var message in messages)
            template.Add(Role(message.Role), message.Content);
        return Encoding.UTF8.GetString(template.Apply());
    }

    private static string Role(ToolMessageRole role) => role switch
    {
        ToolMessageRole.System => "system",
        ToolMessageRole.User => "user",
        ToolMessageRole.Assistant => "assistant",
        ToolMessageRole.Tool => "tool",
        _ => throw new ArgumentOutOfRangeException(
            nameof(role),
            role,
            "The native integration adapter cannot map this ToolMessageRole. Update the test "
            + "adapter and its release assertions before accepting a new public role."),
    };

    private static string RequestFor(ToolChoice choice) => choice.Kind switch
    {
        ToolChoiceKind.None => "Reply with OK.",
        ToolChoiceKind.Required => "Use one tool with its fixed valid value.",
        ToolChoiceKind.Named => "Call echo with its fixed valid value.",
        _ => "Reply with OK or use one suitable tool.",
    };

    private static ToolDefinition EchoTool() => ToolDefinition.Parse(
        "echo",
        "Returns the supplied fixed test value.",
        FixedValueSchema);

    private static ToolDefinition LookupTool() => ToolDefinition.Parse(
        "lookup",
        "Looks up the supplied fixed test value.",
        FixedValueSchema);

    private static TestCaseData Case(ToolChoice choice, bool allowRefusal) =>
        new TestCaseData(choice, allowRefusal).SetName(
            $"Native_inference_{choice.ToString().Replace('(', '_').Replace(")", string.Empty)}_"
            + $"refusal_{allowRefusal}");

    private const string FixedValueSchema =
        "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\","
        + "\"const\":\"ok\"}},\"required\":[\"value\"],\"additionalProperties\":false}";
}

using System.Text.Json;

namespace LlamaSharp.ToolCallEnvelopes.Tests;

[TestFixture]
public sealed class TurnPolicyCombinationTests
{
    public static IEnumerable<TestCaseData> EveryLegalTurnPolicy
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
                                    $"Policy_tools_{toolCount}_refusal_{allowRefusal}_calls_{maxCalls}_"
                                    + choice.ToString().Replace('(', '_').Replace(")", string.Empty));
                        }
        }
    }

    [TestCaseSource(nameof(EveryLegalTurnPolicy))]
    public void EveryLegalTurnPolicy_EnforcesEveryResponseBranch(
        int toolCount,
        bool allowRefusal,
        int maxCalls,
        ToolChoice choice)
    {
        var tools = Tools(toolCount);
        var plan = TestCatalog.Plan(
            allowRefusal: allowRefusal,
            maxCalls: maxCalls,
            tools: tools);
        var turn = plan.CreateTurn("Policy.", [], choice);
        var allowsFinal = choice.Kind is ToolChoiceKind.Auto or ToolChoiceKind.None;
        var allowsCalls = toolCount > 0 && choice.Kind != ToolChoiceKind.None;

        AssertBranch(
            turn,
            """{"text":"done"}""",
            allowsFinal,
            ToolEnvelopeErrorCode.ToolCallsRequired);
        AssertBranch(
            turn,
            """{"refusal":"declined"}""",
            allowRefusal && allowsFinal,
            ToolEnvelopeErrorCode.RefusalNotAllowed);

        foreach (var tool in tools)
        {
            var output = ToolRequest(tool.Name, ArgumentsFor(tool.Name));
            var matchingNamedChoice = choice.Kind != ToolChoiceKind.Named
                                      || choice.ToolName == tool.Name;
            var expectedError = !allowsCalls
                ? ToolEnvelopeErrorCode.ToolCallsNotAllowed
                : ToolEnvelopeErrorCode.WrongTool;
            AssertBranch(turn, output, allowsCalls && matchingNamedChoice, expectedError);
        }

        if (allowsCalls)
        {
            var selectedName = choice.Kind == ToolChoiceKind.Named
                ? choice.ToolName!
                : tools[0].Name;
            var exactLimit = ToolRequestBatch(selectedName, maxCalls);
            var overLimit = ToolRequestBatch(selectedName, maxCalls + 1);

            turn.Parse(exactLimit).Should().BeOfType<ToolEnvelopeOutcome.ToolRequest>()
                .Which.Calls.Should().HaveCount(maxCalls);
            turn.TryParse(overLimit, out _, out var error).Should().BeFalse();
            error!.Code.Should().Be(ToolEnvelopeErrorCode.TooManyToolCalls);
        }

        turn.Choice.Should().Be(choice);
        turn.Grammar.Should().NotBeNullOrWhiteSpace();
        turn.GrammarCacheKey.Should().MatchRegex("^[0-9A-F]{64}$");
    }

    [Test]
    public void GeneratedPolicyMatrix_CoversEveryLegalCombinationExactlyOnce()
    {
        var cases = EveryLegalTurnPolicy.ToArray();

        cases.Should().HaveCount(44);
        cases.Select(test => test.TestName).Should().OnlyHaveUniqueItems();
    }

    private static void AssertBranch(
        ToolEnvelopeTurn turn,
        string output,
        bool shouldSucceed,
        ToolEnvelopeErrorCode expectedError)
    {
        var succeeded = turn.TryParse(output, out var outcome, out var error);

        succeeded.Should().Be(shouldSucceed, $"choice {turn.Choice} parsing {output}");
        if (shouldSucceed)
        {
            outcome.Should().NotBeNull();
            error.Should().BeNull();
        }
        else
        {
            outcome.Should().BeNull();
            error!.Code.Should().Be(expectedError);
        }
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

    private static string ToolRequestBatch(string name, int count) =>
        "{\"tool_calls\":["
        + string.Join(
            ",",
            Enumerable.Range(0, count).Select(_ =>
                $"{{\"name\":{JsonSerializer.Serialize(name)},\"arguments\":"
                + ArgumentsFor(name)
                + "}"))
        + "]}";

    private static string ToolRequest(string name, string arguments) =>
        $"{{\"tool_calls\":[{{\"name\":{JsonSerializer.Serialize(name)},"
        + $"\"arguments\":{arguments}}}]}}";

    private static string ArgumentsFor(string name) => name switch
    {
        "get_weather" => """{"city":"Zagreb","unit":"celsius"}""",
        "search" => """{"query":"Zagreb"}""",
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };
}

namespace LlamaSharp.ToolCallEnvelopes.Tests;

[TestFixture]
public sealed class ManagedRunnerCombinationTests
{
    public static IEnumerable<TestCaseData> EveryManagedOptionCombination
    {
        get
        {
            var initialChoices = new[]
            {
                ToolChoice.Auto,
                ToolChoice.None,
                ToolChoice.Required,
                ToolChoice.Named("get_weather"),
            };
            var followUpChoices = new[] { ToolChoice.Auto, ToolChoice.None };

            foreach (var initial in initialChoices)
                foreach (var followUp in followUpChoices)
                    foreach (var allowRefusal in new[] { false, true })
                        foreach (var maxAttempts in new[] { 1, 2 })
                            foreach (var maxTurns in new[] { 1, 2 })
                                foreach (var observe in new[] { false, true })
                                {
                                    yield return new TestCaseData(
                                            initial,
                                            followUp,
                                            allowRefusal,
                                            maxAttempts,
                                            maxTurns,
                                            observe)
                                        .SetName(
                                            $"Managed_initial_{Safe(initial)}_follow_{Safe(followUp)}_"
                                            + $"refusal_{allowRefusal}_attempts_{maxAttempts}_turns_{maxTurns}_"
                                            + $"observer_{observe}");
                                }
        }
    }

    [TestCaseSource(nameof(EveryManagedOptionCombination))]
    public async Task EveryManagedOptionCombination_FollowsOneDeterministicPipeline(
        ToolChoice initial,
        ToolChoice followUp,
        bool allowRefusal,
        int maxAttempts,
        int maxTurns,
        bool observe)
    {
        var requestsTool = initial.Kind is ToolChoiceKind.Required or ToolChoiceKind.Named
                           || initial.Kind == ToolChoiceKind.Auto && maxTurns == 2;
        var reachesFollowUp = requestsTool && maxTurns == 2;
        var semanticTurns = reachesFollowUp ? 2 : 1;
        var outputs = new List<string>();
        AddAttempt(outputs, requestsTool
            ? TestCatalog.ToolRequest(
                "get_weather",
                """{"city":"Zagreb","unit":"celsius"}""")
            : FinalOutput(allowRefusal), maxAttempts);
        if (reachesFollowUp)
            AddAttempt(outputs, FinalOutput(allowRefusal), maxAttempts);

        var events = new List<ToolRunEvent>();
        ToolRunObserver? observer = observe
            ? (update, _) =>
            {
                events.Add(update);
                return ValueTask.CompletedTask;
            }
        : null;
        var executor = new QueueExecutor(outputs.ToArray());
        var dispatchCount = 0;

        var result = await ToolEnvelopeRunner.RunAsync(
            executor,
            TestCatalog.Plan(allowRefusal: allowRefusal),
            "Policy.",
            [ToolMessage.User("Request.")],
            (_, _) =>
            {
                dispatchCount++;
                return ValueTask.FromResult("{}");
            },
            new ToolRunOptions
            {
                InitialChoice = initial,
                FollowUpChoice = followUp,
                MaxAttemptsPerTurn = maxAttempts,
                MaxModelTurns = maxTurns,
                Observer = observer,
            });

        if (requestsTool && !reachesFollowUp)
        {
            result.Should().BeOfType<ToolRunResult.Failed>()
                .Which.Failure.Code.Should().Be(ToolRunFailureCode.ModelTurnLimitReached);
        }
        else
        {
            var completed = result.Should().BeOfType<ToolRunResult.Completed>().Which;
            if (allowRefusal)
                completed.Outcome.Should().BeOfType<ToolEnvelopeOutcome.Refusal>();
            else
                completed.Outcome.Should().BeOfType<ToolEnvelopeOutcome.AssistantMessage>();
        }

        result.Executions.Should().HaveCount(requestsTool ? 1 : 0);
        dispatchCount.Should().Be(requestsTool ? 1 : 0);
        executor.CallCount.Should().Be(semanticTurns * maxAttempts);

        var expectedChoices = Enumerable.Repeat(initial, maxAttempts).ToList();
        if (reachesFollowUp)
            expectedChoices.AddRange(Enumerable.Repeat(followUp, maxAttempts));
        executor.Turns.Select(turn => turn.Choice).Should().Equal(expectedChoices);

        if (observe)
        {
            events.OfType<ToolRunEvent.AttemptStarted>()
                .Should().HaveCount(semanticTurns * maxAttempts);
            events.OfType<ToolRunEvent.AttemptRejected>()
                .Should().HaveCount(maxAttempts == 2 ? semanticTurns : 0);
            events.OfType<ToolRunEvent.AttemptAccepted>().Should().HaveCount(semanticTurns);
            events.OfType<ToolRunEvent.ToolDispatchStarted>()
                .Should().HaveCount(requestsTool ? 1 : 0);
            events.OfType<ToolRunEvent.ToolDispatchCompleted>()
                .Should().HaveCount(requestsTool ? 1 : 0);
        }
        else
        {
            events.Should().BeEmpty();
        }
    }

    [Test]
    public void GeneratedManagedMatrix_CoversEveryCombinationExactlyOnce()
    {
        var cases = EveryManagedOptionCombination.ToArray();

        cases.Should().HaveCount(128);
        cases.Select(test => test.TestName).Should().OnlyHaveUniqueItems();
    }

    private static void AddAttempt(List<string> outputs, string validOutput, int maxAttempts)
    {
        if (maxAttempts == 2)
            outputs.Add("not json");
        outputs.Add(validOutput);
    }

    private static string FinalOutput(bool allowRefusal) => allowRefusal
        ? """{"refusal":"declined"}"""
        : """{"text":"done"}""";

    private static string Safe(ToolChoice choice) =>
        choice.ToString().Replace('(', '_').Replace(")", string.Empty);
}

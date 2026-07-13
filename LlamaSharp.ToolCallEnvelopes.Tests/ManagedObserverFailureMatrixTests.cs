namespace LlamaSharp.ToolCallEnvelopes.Tests;

[TestFixture]
public sealed class ManagedObserverFailureMatrixTests
{
    private static readonly Type[] EventTypes =
    [
        typeof(ToolRunEvent.AttemptStarted),
        typeof(ToolRunEvent.AssistantTextDelta),
        typeof(ToolRunEvent.RefusalDelta),
        typeof(ToolRunEvent.ToolArgumentsDelta),
        typeof(ToolRunEvent.AttemptAccepted),
        typeof(ToolRunEvent.AttemptRejected),
        typeof(ToolRunEvent.ToolDispatchStarted),
        typeof(ToolRunEvent.ToolDispatchCompleted),
    ];

    public static IEnumerable<TestCaseData> EveryObserverEvent =>
        EventTypes.Select(type => new TestCaseData(type).SetName($"Observer_failure_at_{type.Name}"));

    [TestCaseSource(nameof(EveryObserverEvent))]
    public async Task ObserverFailureAtEveryEvent_StopsAtAnExactCommittedBoundary(Type eventType)
    {
        var setup = SetupFor(eventType);
        var observed = new List<ToolRunEvent>();
        var dispatchCount = 0;
        var executor = new QueueExecutor(setup.Output);

        var result = await ToolEnvelopeRunner.RunAsync(
            executor,
            setup.Plan,
            "Policy.",
            [ToolMessage.User("Request.")],
            (_, _) =>
            {
                dispatchCount++;
                return ValueTask.FromResult("{}");
            },
            new ToolRunOptions
            {
                InitialChoice = setup.Choice,
                MaxAttemptsPerTurn = setup.MaxAttempts,
                Observer = (update, _) =>
                {
                    observed.Add(update);
                    if (update.GetType() == eventType)
                        throw new TestAdapterException(eventType.Name);
                    return ValueTask.CompletedTask;
                },
            });

        var failed = result.Should().BeOfType<ToolRunResult.Failed>().Which;
        failed.Failure.Code.Should().Be(ToolRunFailureCode.ObserverFailed);
        failed.Failure.Exception.Should().BeOfType<TestAdapterException>();
        failed.Failure.Message.Should().Contain(eventType.Name)
            .And.Contain("model turn 0, attempt 0")
            .And.Contain("Fix or remove the observer");
        observed[^1].GetType().Should().Be(eventType);

        var dispatchCompleted = eventType == typeof(ToolRunEvent.ToolDispatchCompleted);
        dispatchCount.Should().Be(dispatchCompleted ? 1 : 0);
        failed.Executions.Should().HaveCount(dispatchCompleted ? 1 : 0);
        executor.CallCount.Should().Be(eventType == typeof(ToolRunEvent.AttemptStarted) ? 0 : 1);
    }

    [Test]
    public void ObserverFailureMatrix_CoversEveryPublicEventType()
    {
        var publicEvents = typeof(ToolRunEvent).GetNestedTypes()
            .Where(type => type.IsNestedPublic)
            .ToArray();

        EventTypes.Should().BeEquivalentTo(publicEvents);
    }

    private static ObserverSetup SetupFor(Type eventType)
    {
        if (eventType == typeof(ToolRunEvent.RefusalDelta))
        {
            return new ObserverSetup(
                TestCatalog.Plan(allowRefusal: true),
                ToolChoice.None,
                """{"refusal":"declined"}""",
                1);
        }

        if (eventType == typeof(ToolRunEvent.AssistantTextDelta)
            || eventType == typeof(ToolRunEvent.AttemptAccepted)
            || eventType == typeof(ToolRunEvent.AttemptStarted))
        {
            return new ObserverSetup(
                TestCatalog.Plan(),
                ToolChoice.None,
                """{"text":"done"}""",
                1);
        }

        if (eventType == typeof(ToolRunEvent.AttemptRejected))
        {
            return new ObserverSetup(
                TestCatalog.Plan(),
                ToolChoice.Auto,
                "not json",
                2);
        }

        return new ObserverSetup(
            TestCatalog.Plan(),
            ToolChoice.Required,
            TestCatalog.ToolRequest(
                "get_weather",
                """{"city":"Zagreb","unit":"celsius"}"""),
            1);
    }

    private sealed record ObserverSetup(
        ToolEnvelopePlan Plan,
        ToolChoice Choice,
        string Output,
        int MaxAttempts);
}

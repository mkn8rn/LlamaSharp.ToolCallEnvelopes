namespace LlamaSharp.ToolCallEnvelopes.Tests;

[TestFixture]
public sealed class CallerCancellationMatrixTests
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

    public static IEnumerable<TestCaseData> EveryObserverBoundary =>
        EventTypes.Select(type =>
            new TestCaseData(type).SetName($"Caller_cancellation_at_{type.Name}"));

    public static IEnumerable<TestCaseData> EveryInferenceBoundary
    {
        get
        {
            yield return new TestCaseData(CancellationBoundary.InferAsync);
            yield return new TestCaseData(CancellationBoundary.GetAsyncEnumerator);
            yield return new TestCaseData(CancellationBoundary.MoveNextAsync);
            yield return new TestCaseData(CancellationBoundary.DisposeAsync);
        }
    }

    [Test]
    public async Task PreCanceledRun_DoesNotStartObserverInferenceOrDispatch()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var executor = new QueueExecutor("""{"text":"unused"}""");
        var observerCount = 0;
        var dispatchCount = 0;

        Func<Task> run = async () => await ToolEnvelopeRunner.RunAsync(
            executor,
            TestCatalog.Plan(),
            "Policy.",
            [ToolMessage.User("Request.")],
            (_, _) =>
            {
                dispatchCount++;
                return ValueTask.FromResult("{}");
            },
            new ToolRunOptions
            {
                Observer = (_, _) =>
                {
                    observerCount++;
                    return ValueTask.CompletedTask;
                },
            },
            cancellation.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
        observerCount.Should().Be(0);
        executor.CallCount.Should().Be(0);
        dispatchCount.Should().Be(0);
    }

    [TestCaseSource(nameof(EveryObserverBoundary))]
    public async Task CallerCancellationAtEveryObserverBoundary_StopsBeforeTheNextAction(
        Type eventType)
    {
        using var cancellation = new CancellationTokenSource();
        var setup = SetupFor(eventType);
        var observed = new List<ToolRunEvent>();
        var dispatchCount = 0;
        var executor = new QueueExecutor(setup.Output);

        Func<Task> run = async () => await ToolEnvelopeRunner.RunAsync(
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
                        cancellation.Cancel();
                    return ValueTask.CompletedTask;
                },
            },
            cancellation.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
        observed[^1].GetType().Should().Be(eventType);

        var completedDispatch = eventType == typeof(ToolRunEvent.ToolDispatchCompleted);
        dispatchCount.Should().Be(completedDispatch ? 1 : 0);
        executor.CallCount.Should().Be(eventType == typeof(ToolRunEvent.AttemptStarted) ? 0 : 1);
    }

    [TestCaseSource(nameof(EveryInferenceBoundary))]
    public async Task CallerCancellationAtEveryInferenceBoundary_IsNeverNormalizedAsFailure(
        CancellationBoundary boundary)
    {
        using var cancellation = new CancellationTokenSource();
        var sequence = new CancelingAsyncEnumerable(cancellation, boundary);
        var executor = new QueueExecutor((_, _) =>
        {
            if (boundary == CancellationBoundary.InferAsync)
                cancellation.Cancel();
            return sequence;
        });

        Func<Task> run = async () => await ToolEnvelopeRunner.RunAsync(
            executor,
            TestCatalog.Plan(),
            "Policy.",
            [ToolMessage.User("Request.")],
            (_, _) => ValueTask.FromResult("{}"),
            cancellationToken: cancellation.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
        executor.CallCount.Should().Be(1);
        sequence.WasDisposed.Should().BeTrue();
    }

    [Test]
    public async Task DispatcherThatIgnoresNewCallerCancellation_CannotCommitItsResult()
    {
        using var cancellation = new CancellationTokenSource();
        var request = TestCatalog.ToolRequest(
            "get_weather",
            """{"city":"Zagreb","unit":"celsius"}""");
        var completedEvents = 0;

        Func<Task> run = async () => await ToolEnvelopeRunner.RunAsync(
            new QueueExecutor(request),
            TestCatalog.Plan(),
            "Policy.",
            [ToolMessage.User("Request.")],
            (_, _) =>
            {
                cancellation.Cancel();
                return ValueTask.FromResult("ignored cancellation");
            },
            new ToolRunOptions
            {
                InitialChoice = ToolChoice.Required,
                Observer = (update, _) =>
                {
                    if (update is ToolRunEvent.ToolDispatchCompleted)
                        completedEvents++;
                    return ValueTask.CompletedTask;
                },
            },
            cancellation.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
        completedEvents.Should().Be(0);
    }

    [Test]
    public void CancellationMatrix_CoversEveryPublicObserverEvent()
    {
        var publicEvents = typeof(ToolRunEvent).GetNestedTypes()
            .Where(type => type.IsNestedPublic)
            .ToArray();

        EventTypes.Should().BeEquivalentTo(publicEvents);
    }

    private static CancellationSetup SetupFor(Type eventType)
    {
        if (eventType == typeof(ToolRunEvent.RefusalDelta))
        {
            return new CancellationSetup(
                TestCatalog.Plan(allowRefusal: true),
                ToolChoice.None,
                """{"refusal":"declined"}""",
                1);
        }

        if (eventType == typeof(ToolRunEvent.AssistantTextDelta)
            || eventType == typeof(ToolRunEvent.AttemptAccepted)
            || eventType == typeof(ToolRunEvent.AttemptStarted))
        {
            return new CancellationSetup(
                TestCatalog.Plan(),
                ToolChoice.None,
                """{"text":"done"}""",
                1);
        }

        if (eventType == typeof(ToolRunEvent.AttemptRejected))
        {
            return new CancellationSetup(
                TestCatalog.Plan(),
                ToolChoice.Auto,
                "not json",
                2);
        }

        return new CancellationSetup(
            TestCatalog.Plan(),
            ToolChoice.Required,
            TestCatalog.ToolRequest(
                "get_weather",
                """{"city":"Zagreb","unit":"celsius"}"""),
            1);
    }

    public enum CancellationBoundary
    {
        InferAsync,
        GetAsyncEnumerator,
        MoveNextAsync,
        DisposeAsync,
    }

    private sealed record CancellationSetup(
        ToolEnvelopePlan Plan,
        ToolChoice Choice,
        string Output,
        int MaxAttempts);

    private sealed class CancelingAsyncEnumerable(
        CancellationTokenSource cancellation,
        CancellationBoundary boundary)
        : IAsyncEnumerable<string>, IAsyncEnumerator<string>
    {
        private int _moveCount;

        internal bool WasDisposed { get; private set; }

        public string Current => """{"text":"done"}""";

        public IAsyncEnumerator<string> GetAsyncEnumerator(
            CancellationToken cancellationToken = default)
        {
            if (boundary == CancellationBoundary.GetAsyncEnumerator)
                cancellation.Cancel();
            return this;
        }

        public ValueTask<bool> MoveNextAsync()
        {
            if (_moveCount++ > 0)
                return ValueTask.FromResult(false);
            if (boundary == CancellationBoundary.MoveNextAsync)
                cancellation.Cancel();
            return ValueTask.FromResult(true);
        }

        public ValueTask DisposeAsync()
        {
            WasDisposed = true;
            if (boundary == CancellationBoundary.DisposeAsync)
                cancellation.Cancel();
            return ValueTask.CompletedTask;
        }
    }
}

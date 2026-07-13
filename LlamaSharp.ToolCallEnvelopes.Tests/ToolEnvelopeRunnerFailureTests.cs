namespace LlamaSharp.ToolCallEnvelopes.Tests;

[TestFixture]
public sealed class ToolEnvelopeRunnerFailureTests
{
    public static IEnumerable<TestCaseData> AdapterBypassCases
    {
        get
        {
            yield return Bypass(
                "required text",
                TestCatalog.Plan(),
                ToolChoice.Required,
                """{"text":"bypass"}""",
                ToolEnvelopeErrorCode.ToolCallsRequired);
            yield return Bypass(
                "none calls",
                TestCatalog.Plan(),
                ToolChoice.None,
                TestCatalog.ToolRequest(
                    "get_weather",
                    """{"city":"Zagreb","unit":"celsius"}"""),
                ToolEnvelopeErrorCode.ToolCallsNotAllowed);
            yield return Bypass(
                "disabled refusal",
                TestCatalog.Plan(),
                ToolChoice.Auto,
                """{"refusal":"bypass"}""",
                ToolEnvelopeErrorCode.RefusalNotAllowed);
            yield return Bypass(
                "wrong named call",
                TestCatalog.Plan(tools: [TestCatalog.Weather(), TestCatalog.Search()]),
                ToolChoice.Named("search"),
                TestCatalog.ToolRequest(
                    "get_weather",
                    """{"city":"Zagreb","unit":"celsius"}"""),
                ToolEnvelopeErrorCode.WrongTool);
            yield return Bypass(
                "unknown catalog name",
                TestCatalog.Plan(),
                ToolChoice.Auto,
                TestCatalog.ToolRequest("delete_everything", "{}"),
                ToolEnvelopeErrorCode.UnknownTool);
            yield return Bypass(
                "too many calls",
                TestCatalog.Plan(),
                ToolChoice.Auto,
                """
                {"tool_calls":[
                  {"name":"get_weather","arguments":{"city":"Zagreb","unit":"celsius"}},
                  {"name":"get_weather","arguments":{"city":"Split","unit":"celsius"}}
                ]}
                """,
                ToolEnvelopeErrorCode.TooManyToolCalls);
            yield return Bypass(
                "schema-invalid arguments",
                TestCatalog.Plan(),
                ToolChoice.Auto,
                TestCatalog.ToolRequest("get_weather", "{}"),
                ToolEnvelopeErrorCode.SchemaViolation);

            var smallLimits = ToolEnvelopeLimits.Constrained with { MaxEnvelopeCharacters = 30 };
            yield return Bypass(
                "excessive output",
                TestCatalog.Plan(limits: smallLimits),
                ToolChoice.Auto,
                new string('x', 31),
                ToolEnvelopeErrorCode.OutputTooLarge);
        }
    }

    [TestCaseSource(nameof(AdapterBypassCases))]
    public async Task RunAsync_NeverDispatchesOutputThatBypassesTheGrammar(
        ToolEnvelopePlan plan,
        ToolChoice choice,
        string output,
        ToolEnvelopeErrorCode expectedError)
    {
        var dispatchCount = 0;

        var result = await ToolEnvelopeRunner.RunAsync(
            new QueueExecutor(output),
            plan,
            "Policy.",
            [ToolMessage.User("Request.")],
            (_, _) =>
            {
                dispatchCount++;
                return ValueTask.FromResult("{}");
            },
            new ToolRunOptions
            {
                InitialChoice = choice,
                MaxModelTurns = 1,
                MaxAttemptsPerTurn = 1,
            });

        var failed = result.Should().BeOfType<ToolRunResult.Failed>().Which;
        failed.Failure.Code.Should().Be(ToolRunFailureCode.InvalidModelOutput);
        failed.Failure.EnvelopeError!.Code.Should().Be(expectedError);
        dispatchCount.Should().Be(0);
    }

    [Test]
    public async Task RunAsync_ReportsRepairExhaustionWithoutConsumingExtraTurns()
    {
        var events = new List<ToolRunEvent>();
        var executor = new QueueExecutor("not json", """{"text":"still forbidden"}""");

        var result = await ToolEnvelopeRunner.RunAsync(
            executor,
            TestCatalog.Plan(),
            "Use a tool.",
            [ToolMessage.User("Weather?")],
            (_, _) => ValueTask.FromResult("{}"),
            new ToolRunOptions
            {
                InitialChoice = ToolChoice.Required,
                MaxModelTurns = 1,
                MaxAttemptsPerTurn = 2,
                Observer = (update, _) =>
                {
                    events.Add(update);
                    return ValueTask.CompletedTask;
                },
            });

        var failed = result.Should().BeOfType<ToolRunResult.Failed>().Which;
        failed.Failure.Code.Should().Be(ToolRunFailureCode.InvalidModelOutput);
        failed.Failure.TurnIndex.Should().Be(0);
        failed.Failure.AttemptIndex.Should().Be(1);
        executor.CallCount.Should().Be(2);
        events.OfType<ToolRunEvent.AttemptRejected>().Should().HaveCount(2);
        events[^1].Should().BeOfType<ToolRunEvent.AttemptRejected>();
    }

    [Test]
    public async Task RunAsync_ReportsTheSemanticTurnLimitAfterSuccessfulDispatch()
    {
        var result = await ToolEnvelopeRunner.RunAsync(
            new QueueExecutor(TestCatalog.ToolRequest(
                "get_weather",
                """{"city":"Zagreb","unit":"celsius"}""")),
            TestCatalog.Plan(),
            "Use a tool.",
            [ToolMessage.User("Weather?")],
            (_, _) => ValueTask.FromResult("{}"),
            new ToolRunOptions
            {
                InitialChoice = ToolChoice.Required,
                MaxModelTurns = 1,
            });

        var failed = result.Should().BeOfType<ToolRunResult.Failed>().Which;
        failed.Failure.Code.Should().Be(ToolRunFailureCode.ModelTurnLimitReached);
        failed.Executions.Should().ContainSingle();
    }

    [Test]
    public async Task RunAsync_RejectsNullOversizedAndThrowingToolResults()
    {
        var request = TestCatalog.ToolRequest(
            "get_weather",
            """{"city":"Zagreb","unit":"celsius"}""");
        var plan = TestCatalog.Plan(
            limits: ToolEnvelopeLimits.Constrained with { MaxToolResultCharacters = 5 });

        var nullResult = await RunOneTool(
            plan,
            request,
            (_, _) => ValueTask.FromResult<string>(null!));
        var largeResult = await RunOneTool(
            plan,
            request,
            (_, _) => ValueTask.FromResult("123456"));
        var exceptionResult = await RunOneTool(
            plan,
            request,
            (_, _) => throw new TestAdapterException("tool"));
        var selfCanceled = await RunOneTool(
            plan,
            request,
            (_, _) => ValueTask.FromCanceled<string>(new CancellationToken(canceled: true)));

        nullResult.Failure.Code.Should().Be(ToolRunFailureCode.ToolResultWasNull);
        nullResult.Failure.Message.Should().Contain("returned null")
            .And.Contain("Return an empty string");
        largeResult.Failure.Code.Should().Be(ToolRunFailureCode.ToolResultTooLarge);
        largeResult.Failure.Message.Should().Contain("returned 6 characters")
            .And.Contain("permits at most 5");
        exceptionResult.Failure.Code.Should().Be(ToolRunFailureCode.ToolExecutionFailed);
        exceptionResult.Failure.Exception.Should().BeOfType<TestAdapterException>();
        exceptionResult.Failure.Message.Should().Contain("TestAdapterException: tool")
            .And.Contain("manual control flow");
        selfCanceled.Failure.Code.Should().Be(ToolRunFailureCode.ToolExecutionFailed);
    }

    [TestCase("synchronous")]
    [TestCase("get-enumerator")]
    [TestCase("move-next")]
    [TestCase("dispose")]
    public async Task RunAsync_NormalizesEveryExecutorFailureBoundary(string boundary)
    {
        ThrowingAsyncEnumerable? enumerable = null;
        Func<ToolEnvelopeTurn, CancellationToken, IAsyncEnumerable<string>> run = boundary switch
        {
            "synchronous" => (_, _) => throw new TestAdapterException("synchronous"),
            "get-enumerator" => (_, _) => enumerable = new ThrowingAsyncEnumerable(
                throwOnGetEnumerator: true),
            "move-next" => (_, _) => enumerable = new ThrowingAsyncEnumerable(
                throwOnMoveNext: true),
            "dispose" => (_, _) => enumerable = new ThrowingAsyncEnumerable(
                """{"text":"valid"}""",
                throwOnDispose: true),
            _ => throw new ArgumentOutOfRangeException(nameof(boundary)),
        };

        var result = await ToolEnvelopeRunner.RunAsync(
            new QueueExecutor(run),
            TestCatalog.Plan(),
            "Answer.",
            [ToolMessage.User("Hello")],
            (_, _) => ValueTask.FromResult("{}"),
            new ToolRunOptions { MaxAttemptsPerTurn = 1 });

        var failed = result.Should().BeOfType<ToolRunResult.Failed>().Which;
        failed.Failure.Code.Should().Be(ToolRunFailureCode.InferenceFailed);
        failed.Failure.Exception.Should().BeOfType<TestAdapterException>();
        failed.Failure.Message.Should().Contain($"TestAdapterException: {boundary}")
            .And.Contain("repair the adapter");
        if (enumerable is not null && boundary != "get-enumerator")
            enumerable.WasDisposed.Should().BeTrue();
    }

    [Test]
    public async Task RunAsync_PreservesEnumerationFailureWhenDisposalAlsoFails()
    {
        var enumerable = new ThrowingAsyncEnumerable(
            throwOnMoveNext: true,
            throwOnDispose: true);

        var result = await ToolEnvelopeRunner.RunAsync(
            new QueueExecutor((_, _) => enumerable),
            TestCatalog.Plan(),
            "Answer.",
            [ToolMessage.User("Hello")],
            (_, _) => ValueTask.FromResult("{}"),
            new ToolRunOptions { MaxAttemptsPerTurn = 1 });

        var failed = result.Should().BeOfType<ToolRunResult.Failed>().Which;
        failed.Failure.Code.Should().Be(ToolRunFailureCode.InferenceFailed);
        failed.Failure.Exception.Should().BeOfType<TestAdapterException>()
            .Which.Message.Should().Be("move-next");
        enumerable.WasDisposed.Should().BeTrue();
    }

    [Test]
    public async Task RunAsync_ReportsDisposalFailureAfterEarlyModelRejection()
    {
        var enumerable = new ThrowingAsyncEnumerable(
            new string('x', 31),
            throwOnDispose: true);
        var plan = TestCatalog.Plan(
            limits: ToolEnvelopeLimits.Constrained with { MaxEnvelopeCharacters = 30 });

        var result = await ToolEnvelopeRunner.RunAsync(
            new QueueExecutor((_, _) => enumerable),
            plan,
            "Answer.",
            [ToolMessage.User("Hello")],
            (_, _) => ValueTask.FromResult("{}"),
            new ToolRunOptions { MaxAttemptsPerTurn = 1 });

        var failed = result.Should().BeOfType<ToolRunResult.Failed>().Which;
        failed.Failure.Code.Should().Be(ToolRunFailureCode.InferenceFailed);
        failed.Failure.Exception.Should().BeOfType<TestAdapterException>()
            .Which.Message.Should().Be("dispose");
        enumerable.DisposalSawCancellation.Should().BeTrue();
    }

    [Test]
    public async Task RunAsync_PreservesObserverFailureWhenDisposalAlsoFails()
    {
        var enumerable = new ThrowingAsyncEnumerable(
            "{\"text\":\"done\"}",
            throwOnDispose: true);

        var result = await ToolEnvelopeRunner.RunAsync(
            new QueueExecutor((_, _) => enumerable),
            TestCatalog.Plan(),
            "Answer.",
            [ToolMessage.User("Hello")],
            (_, _) => ValueTask.FromResult("{}"),
            new ToolRunOptions
            {
                Observer = (update, _) => update is ToolRunEvent.AssistantTextDelta
                    ? throw new TestAdapterException("observer-primary")
                    : ValueTask.CompletedTask,
            });

        var failed = result.Should().BeOfType<ToolRunResult.Failed>().Which;
        failed.Failure.Code.Should().Be(ToolRunFailureCode.ObserverFailed);
        failed.Failure.Exception.Should().BeOfType<TestAdapterException>()
            .Which.Message.Should().Be("observer-primary");
        enumerable.WasDisposed.Should().BeTrue();
        enumerable.DisposalSawCancellation.Should().BeTrue();
    }

    [TestCase("null-stream")]
    [TestCase("null-enumerator")]
    public async Task RunAsync_ExplainsEveryNullAsyncStreamContractViolation(string boundary)
    {
        Func<ToolEnvelopeTurn, CancellationToken, IAsyncEnumerable<string>> run = boundary switch
        {
            "null-stream" => (_, _) => null!,
            "null-enumerator" => (_, _) => new NullEnumeratorAsyncEnumerable(),
            _ => throw new ArgumentOutOfRangeException(nameof(boundary)),
        };
        var dispatchCount = 0;

        var result = await ToolEnvelopeRunner.RunAsync(
            new QueueExecutor(run),
            TestCatalog.Plan(),
            "Answer.",
            [ToolMessage.User("Hello")],
            (_, _) =>
            {
                dispatchCount++;
                return ValueTask.FromResult("{}");
            },
            new ToolRunOptions { MaxAttemptsPerTurn = 1 });

        var failed = result.Should().BeOfType<ToolRunResult.Failed>().Which;
        failed.Failure.Code.Should().Be(ToolRunFailureCode.InferenceFailed);
        failed.Failure.Exception.Should().BeOfType<InvalidOperationException>();
        failed.Failure.Message.Should().Contain(
                boundary == "null-stream" ? "InferAsync returned null" : "returned null. Return")
            .And.Contain("No call from this attempt was dispatched")
            .And.Contain("repair the adapter");
        dispatchCount.Should().Be(0);
    }

    [Test]
    public async Task RunAsync_CancelsAndDisposesInferenceAfterEarlyStreamRejection()
    {
        var enumerable = new ThrowingAsyncEnumerable(new string('x', 31));
        var plan = TestCatalog.Plan(
            limits: ToolEnvelopeLimits.Constrained with { MaxEnvelopeCharacters = 30 });

        var result = await ToolEnvelopeRunner.RunAsync(
            new QueueExecutor((_, _) => enumerable),
            plan,
            "Answer.",
            [ToolMessage.User("Hello")],
            (_, _) => ValueTask.FromResult("{}"),
            new ToolRunOptions { MaxAttemptsPerTurn = 1 });

        result.Should().BeOfType<ToolRunResult.Failed>()
            .Which.Failure.EnvelopeError!.Code.Should().Be(ToolEnvelopeErrorCode.OutputTooLarge);
        enumerable.WasDisposed.Should().BeTrue();
        enumerable.DisposalSawCancellation.Should().BeTrue();
    }

    [Test]
    public async Task RunAsync_PreservesCallerCancellationDuringInference()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var act = async () => await ToolEnvelopeRunner.RunAsync(
            new QueueExecutor((_, token) => AsyncSequences.WaitForCancellation(token)),
            TestCatalog.Plan(),
            "Answer.",
            [ToolMessage.User("Hello")],
            (_, _) => ValueTask.FromResult("{}"),
            cancellationToken: cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task RunAsync_PreservesCallerCancellationDuringToolExecution()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var request = TestCatalog.ToolRequest(
            "get_weather",
            """{"city":"Zagreb","unit":"celsius"}""");

        var act = async () => await ToolEnvelopeRunner.RunAsync(
            new QueueExecutor(request),
            TestCatalog.Plan(),
            "Use a tool.",
            [ToolMessage.User("Weather?")],
            async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return "never";
            },
            cancellationToken: cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task RunAsync_ReportsObserverFailureWithoutStartingInference()
    {
        var executor = new QueueExecutor("""{"text":"unused"}""");

        var result = await ToolEnvelopeRunner.RunAsync(
            executor,
            TestCatalog.Plan(),
            "Answer.",
            [ToolMessage.User("Hello")],
            (_, _) => ValueTask.FromResult("{}"),
            new ToolRunOptions
            {
                Observer = (_, _) => throw new TestAdapterException("observer"),
            });

        var failed = result.Should().BeOfType<ToolRunResult.Failed>().Which;
        failed.Failure.Code.Should().Be(ToolRunFailureCode.ObserverFailed);
        failed.Failure.Exception.Should().BeOfType<TestAdapterException>();
        executor.CallCount.Should().Be(0);
    }

    [Test]
    public async Task RunAsync_ValidatesAllOptionsBeforeInference()
    {
        var executor = new QueueExecutor("""{"text":"unused"}""");

        var badFollowUp = async () => await ToolEnvelopeRunner.RunAsync(
            executor,
            TestCatalog.Plan(),
            "Answer.",
            [],
            (_, _) => ValueTask.FromResult("{}"),
            new ToolRunOptions { FollowUpChoice = ToolChoice.Required });
        var badTurns = async () => await ToolEnvelopeRunner.RunAsync(
            executor,
            TestCatalog.Plan(),
            "Answer.",
            [],
            (_, _) => ValueTask.FromResult("{}"),
            new ToolRunOptions { MaxModelTurns = 0 });
        var badAttempts = async () => await ToolEnvelopeRunner.RunAsync(
            executor,
            TestCatalog.Plan(),
            "Answer.",
            [],
            (_, _) => ValueTask.FromResult("{}"),
            new ToolRunOptions { MaxAttemptsPerTurn = 0 });

        await badFollowUp.Should().ThrowAsync<ArgumentException>();
        await badTurns.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await badAttempts.Should().ThrowAsync<ArgumentOutOfRangeException>();
        executor.CallCount.Should().Be(0);
    }

    private static async Task<ToolRunResult.Failed> RunOneTool(
        ToolEnvelopePlan plan,
        string request,
        ToolDispatcher dispatch)
    {
        var result = await ToolEnvelopeRunner.RunAsync(
            new QueueExecutor(request),
            plan,
            "Use a tool.",
            [ToolMessage.User("Weather?")],
            dispatch,
            new ToolRunOptions
            {
                InitialChoice = ToolChoice.Required,
                MaxModelTurns = 1,
            });
        return result.Should().BeOfType<ToolRunResult.Failed>().Which;
    }

    private static TestCaseData Bypass(
        string name,
        ToolEnvelopePlan plan,
        ToolChoice choice,
        string output,
        ToolEnvelopeErrorCode expectedError) =>
        new TestCaseData(plan, choice, output, expectedError)
            .SetName($"Blocks_{name.Replace(' ', '_')}");
}

namespace LlamaSharp.ToolCallEnvelopes.Tests;

[TestFixture]
public sealed class ToolEnvelopeRunnerHappyPathTests
{
    [Test]
    public async Task RunAsync_CompletesADirectAnswerWithoutDispatch()
    {
        var executor = new QueueExecutor("""{"text":"Hello."}""");
        var original = new[] { ToolMessage.User("Say hello.") };
        var dispatchCount = 0;

        var result = await ToolEnvelopeRunner.RunAsync(
            executor,
            TestCatalog.Plan(),
            "Answer clearly.",
            original,
            (_, _) =>
            {
                dispatchCount++;
                return ValueTask.FromResult(string.Empty);
            });

        var completed = result.Should().BeOfType<ToolRunResult.Completed>().Which;
        completed.Outcome.Should().BeAssignableTo<ToolEnvelopeOutcome.Final>();
        completed.Outcome.Should().BeOfType<ToolEnvelopeOutcome.AssistantMessage>()
            .Which.Text.Should().Be("Hello.");
        completed.Executions.Should().BeEmpty();
        completed.Conversation.Should().HaveCount(2);
        original.Should().ContainSingle();
        dispatchCount.Should().Be(0);
    }

    [Test]
    public async Task RunAsync_RequiredToolThenAutoAnswerUsesOneSharedPlan()
    {
        var request = TestCatalog.ToolRequest(
            "get_weather",
            """{"city":"Zagreb","unit":"celsius"}""");
        var executor = new QueueExecutor(request, """{"text":"It is 22 C."}""");
        var events = new List<ToolRunEvent>();
        var plan = TestCatalog.Plan();

        var result = await ToolEnvelopeRunner.RunAsync(
            executor,
            plan,
            "Use current weather data.",
            [ToolMessage.User("Weather in Zagreb?")],
            (call, _) => ValueTask.FromResult("""{"temperature":22}"""),
            new ToolRunOptions
            {
                InitialChoice = ToolChoice.Required,
                FollowUpChoice = ToolChoice.Auto,
                Observer = (update, _) =>
                {
                    events.Add(update);
                    return ValueTask.CompletedTask;
                },
            });

        var completed = result.Should().BeOfType<ToolRunResult.Completed>().Which;
        completed.Outcome.Should().BeOfType<ToolEnvelopeOutcome.AssistantMessage>()
            .Which.Text.Should().Be("It is 22 C.");
        completed.Executions.Should().ContainSingle();
        completed.Executions[0].Call.Name.Should().Be("get_weather");
        completed.Executions[0].Result.Should().Be("""{"temperature":22}""");
        completed.Conversation.Select(message => message.Role).Should().Equal(
            ToolMessageRole.User,
            ToolMessageRole.Assistant,
            ToolMessageRole.Tool,
            ToolMessageRole.Assistant);
        executor.Turns.Select(turn => turn.Choice).Should().Equal(
            ToolChoice.Required,
            ToolChoice.Auto);
        executor.Turns.Should().OnlyContain(turn => turn.GrammarCacheKey.Length == 64);
        events[^1].Should().BeOfType<ToolRunEvent.AttemptAccepted>()
            .Which.Outcome.Should().BeOfType<ToolEnvelopeOutcome.AssistantMessage>();
    }

    [Test]
    public async Task RunAsync_NamedThenNoneUsesTheConfiguredFollowUpChoice()
    {
        var executor = new QueueExecutor(
            TestCatalog.ToolRequest("search", """{"query":"Zagreb"}"""),
            """{"text":"Found Zagreb."}""");
        var plan = TestCatalog.Plan(tools: [TestCatalog.Weather(), TestCatalog.Search()]);

        var result = await ToolEnvelopeRunner.RunAsync(
            executor,
            plan,
            "Search when required.",
            [ToolMessage.User("Find Zagreb.")],
            (_, _) => ValueTask.FromResult("""{"matches":1}"""),
            new ToolRunOptions
            {
                InitialChoice = ToolChoice.Named("search"),
                FollowUpChoice = ToolChoice.None,
            });

        result.Should().BeOfType<ToolRunResult.Completed>();
        executor.Turns.Select(turn => turn.Choice).Should().Equal(
            ToolChoice.Named("search"),
            ToolChoice.None);
        executor.Turns[0].Prompt[0].Content.Should().Contain("tool \"search\"")
            .And.NotContain("tool \"get_weather\"");
    }

    [Test]
    public async Task RunAsync_AllowsMultipleConsecutiveToolTurns()
    {
        var executor = new QueueExecutor(
            TestCatalog.ToolRequest(
                "get_weather",
                """{"city":"Zagreb","unit":"celsius"}"""),
            TestCatalog.ToolRequest(
                "get_weather",
                """{"city":"Split","unit":"celsius"}"""),
            """{"text":"Both cities are warm."}""");

        var result = await ToolEnvelopeRunner.RunAsync(
            executor,
            TestCatalog.Plan(),
            "Compare both cities.",
            [ToolMessage.User("Compare Zagreb and Split.")],
            (call, _) => ValueTask.FromResult(
                $"{{\"city\":{System.Text.Json.JsonSerializer.Serialize(call.Arguments.GetProperty("city").GetString())},\"temperature\":22}}"),
            new ToolRunOptions { MaxModelTurns = 3 });

        var completed = result.Should().BeOfType<ToolRunResult.Completed>().Which;
        completed.Executions.Should().HaveCount(2);
        completed.Executions.Select(execution => execution.TurnIndex).Should().Equal(0, 1);
        executor.Turns.Should().HaveCount(3);
    }

    [Test]
    public async Task RunAsync_RepairsWithinTheSameSemanticTurn()
    {
        var executor = new QueueExecutor(
            """{"text":"not allowed"}""",
            TestCatalog.ToolRequest(
                "get_weather",
                """{"city":"Zagreb","unit":"celsius"}"""));
        var events = new List<ToolRunEvent>();

        var result = await ToolEnvelopeRunner.RunAsync(
            executor,
            TestCatalog.Plan(),
            "Use weather data.",
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

        result.Should().BeOfType<ToolRunResult.Failed>()
            .Which.Failure.Code.Should().Be(ToolRunFailureCode.ModelTurnLimitReached);
        executor.Turns.Should().HaveCount(2);
        executor.Turns.Should().OnlyContain(turn => turn.Choice == ToolChoice.Required);
        executor.Turns[1].Prompt.Should().Contain(message =>
            message.Content.Contains("RETRY_DATA", StringComparison.Ordinal));
        events.OfType<ToolRunEvent.AttemptRejected>().Should().ContainSingle()
            .Which.TurnIndex.Should().Be(0);
        events.OfType<ToolRunEvent.AttemptStarted>().Select(update => update.AttemptIndex)
            .Should().Equal(0, 1);
        events.FindIndex(update => update is ToolRunEvent.AttemptRejected)
            .Should().BeLessThan(events.FindLastIndex(update => update is ToolRunEvent.AttemptStarted));
    }

    [Test]
    public async Task RunAsync_DispatchesMultipleCallsStrictlySequentially()
    {
        const string calls =
            """
            {"tool_calls":[
              {"name":"get_weather","arguments":{"city":"Zagreb","unit":"celsius"}},
              {"name":"get_weather","arguments":{"city":"Split","unit":"celsius"}}
            ]}
            """;
        var executor = new QueueExecutor(calls, """{"text":"Done."}""");
        var active = 0;
        var maximumActive = 0;
        var order = new List<string>();

        var result = await ToolEnvelopeRunner.RunAsync(
            executor,
            TestCatalog.Plan(maxCalls: 2),
            "Get both results.",
            [ToolMessage.User("Both cities.")],
            async (call, cancellationToken) =>
            {
                active++;
                maximumActive = Math.Max(maximumActive, active);
                order.Add(call.Arguments.GetProperty("city").GetString()!);
                await Task.Delay(5, cancellationToken);
                active--;
                return "{}";
            });

        result.Should().BeOfType<ToolRunResult.Completed>();
        maximumActive.Should().Be(1);
        order.Should().Equal("Zagreb", "Split");
    }

    [Test]
    public async Task RunAsync_CompletesARefusalOnlyWhenThePlanAllowsIt()
    {
        var result = await ToolEnvelopeRunner.RunAsync(
            new QueueExecutor("""{"refusal":"Cannot help."}"""),
            TestCatalog.Plan(allowRefusal: true),
            "Refusal is available.",
            [ToolMessage.User("Decline.")],
            (_, _) => ValueTask.FromResult("unused"),
            new ToolRunOptions { InitialChoice = ToolChoice.None });

        var completed = result.Should().BeOfType<ToolRunResult.Completed>().Which;
        completed.Outcome.Should().BeOfType<ToolEnvelopeOutcome.Refusal>()
            .Which.Reason.Should().Be("Cannot help.");
        completed.Conversation[^1].Content.Should().Be("""{"refusal":"Cannot help."}""");
    }
}

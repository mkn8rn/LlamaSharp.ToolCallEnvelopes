namespace LlamaSharp.ToolCallEnvelopes.Tests;

[TestFixture]
public sealed class ConcurrencyTests
{
    [Test]
    public async Task OnePlan_CreatesAndParsesEveryChoiceConcurrently()
    {
        var plan = TestCatalog.Plan(
            allowRefusal: true,
            maxCalls: 2,
            tools: [TestCatalog.Weather(), TestCatalog.Search()]);
        var cases = Enumerable.Range(0, 256).Select(index => (index % 4) switch
        {
            0 => new ParseCase(ToolChoice.Auto, """{"text":"done"}"""),
            1 => new ParseCase(ToolChoice.None, """{"refusal":"declined"}"""),
            2 => new ParseCase(
                ToolChoice.Required,
                TestCatalog.ToolRequest(
                    "get_weather",
                    """{"city":"Zagreb","unit":"celsius"}""")),
            _ => new ParseCase(
                ToolChoice.Named("search"),
                TestCatalog.ToolRequest("search", """{"query":"Zagreb"}""")),
        });

        var outcomes = await Task.WhenAll(cases.Select(item => Task.Run(() =>
        {
            var turn = plan.CreateTurn(
                "Policy.",
                [ToolMessage.User("Request.")],
                item.Choice);
            return (Turn: turn, Outcome: turn.Parse(item.Output));
        })));

        outcomes.Should().HaveCount(256);
        outcomes.Should().OnlyContain(item => item.Turn.GrammarCacheKey.Length == 64);
        outcomes.Select(item => item.Turn.GrammarCacheKey).Distinct().Should().HaveCount(4);
        plan.Tools.Should().HaveCount(2);
    }

    [Test]
    public async Task OnePlan_SupportsIndependentManagedRunsConcurrently()
    {
        var plan = TestCatalog.Plan();
        var dispatchCount = 0;

        var runs = Enumerable.Range(0, 64).Select(async index =>
        {
            var executor = new QueueExecutor(
                TestCatalog.ToolRequest(
                    "get_weather",
                    $$"""{"city":"City {{index}}","unit":"celsius"}"""),
                $$"""{"text":"Result {{index}}"}""");
            var result = await ToolEnvelopeRunner.RunAsync(
                executor,
                plan,
                "Policy.",
                [ToolMessage.User($"Request {index}.")],
                (_, _) =>
                {
                    Interlocked.Increment(ref dispatchCount);
                    return ValueTask.FromResult($"{{\"run\":{index}}}");
                },
                new ToolRunOptions { InitialChoice = ToolChoice.Required });
            return (Index: index, Result: result, Executor: executor);
        });

        var completedRuns = await Task.WhenAll(runs);

        dispatchCount.Should().Be(64);
        completedRuns.Should().OnlyContain(item => item.Result is ToolRunResult.Completed);
        completedRuns.Should().OnlyContain(item => item.Result.Executions.Count == 1);
        completedRuns.Should().OnlyContain(item => item.Executor.CallCount == 2);
        completedRuns.Select(item => item.Index).Should().OnlyHaveUniqueItems();
    }

    [Test]
    public async Task OneTurn_SupportsConcurrentParsersAndIndependentStreamReaders()
    {
        var turn = TestCatalog.Turn();
        const string output = """{"text":"thread safe"}""";

        var results = await Task.WhenAll(Enumerable.Range(0, 128).Select(index => Task.Run(() =>
        {
            if (index % 2 == 0)
                return turn.Parse(output);

            var reader = turn.CreateStreamReader();
            foreach (var character in output)
                reader.Feed(character.ToString());
            return reader.Complete();
        })));

        results.Should().HaveCount(128);
        results.Should().OnlyContain(outcome =>
            outcome.GetType() == typeof(ToolEnvelopeOutcome.AssistantMessage));
        results.Cast<ToolEnvelopeOutcome.AssistantMessage>()
            .Should()
            .OnlyContain(outcome => outcome.Text == "thread safe");
    }

    private sealed record ParseCase(ToolChoice Choice, string Output);
}

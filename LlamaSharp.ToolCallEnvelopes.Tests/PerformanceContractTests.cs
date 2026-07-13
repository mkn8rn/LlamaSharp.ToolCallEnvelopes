using System.Diagnostics;

namespace LlamaSharp.ToolCallEnvelopes.Tests;

[TestFixture]
[NonParallelizable]
public sealed class PerformanceContractTests
{
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(5)]
    [TestCase(20)]
    public void CompileCreateTurnAndParse_RemainBoundedAcrossCatalogSizes(int toolCount)
    {
        var tools = Tools(toolCount);
        var limits = ToolEnvelopeLimits.Constrained with
        {
            MaxTools = 20,
            MaxCatalogPromptCharacters = 100_000,
        };
        ToolEnvelopePlan.Compile(
            tools.Take(Math.Min(1, tools.Length)),
            new ToolEnvelopePlanOptions { Limits = limits });

        var compile = Measure(() => ToolEnvelopePlan.Compile(
            tools,
            new ToolEnvelopePlanOptions { Limits = limits }));
        var plan = compile.Value;
        var firstTurn = plan.CreateTurn("Policy.", [ToolMessage.User("Request.")]);

        var createTurns = Measure(() =>
        {
            ToolEnvelopeTurn? last = null;
            for (var index = 0; index < 100; index++)
                last = plan.CreateTurn("Policy.", [ToolMessage.User("Request.")]);
            return last!;
        });
        var parse = Measure(() =>
        {
            ToolEnvelopeOutcome? last = null;
            for (var index = 0; index < 1_000; index++)
                last = firstTurn.Parse("{\"text\":\"done\"}");
            return last!;
        });

        TestContext.Progress.WriteLine(
            $"PERF tools={toolCount} compile_ms={compile.Elapsed.TotalMilliseconds:F2} "
            + $"compile_bytes={compile.AllocatedBytes} create_100_ms="
            + $"{createTurns.Elapsed.TotalMilliseconds:F2} create_100_bytes="
            + $"{createTurns.AllocatedBytes} parse_1000_ms={parse.Elapsed.TotalMilliseconds:F2} "
            + $"parse_1000_bytes={parse.AllocatedBytes}");

        plan.Metrics.ToolCount.Should().Be(toolCount);
        createTurns.Value.Grammar.Should().BeSameAs(firstTurn.Grammar);
        createTurns.Value.GrammarCacheKey.Should().Be(firstTurn.GrammarCacheKey);
        parse.Value.Should().BeOfType<ToolEnvelopeOutcome.AssistantMessage>();
        compile.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        compile.AllocatedBytes.Should().BeLessThan(64L * 1024 * 1024);
        createTurns.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        createTurns.AllocatedBytes.Should().BeLessThan(32L * 1024 * 1024);
        parse.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        parse.AllocatedBytes.Should().BeLessThan(64L * 1024 * 1024);
    }

    [Test]
    public void ConstrainedDefaults_RejectTwentyToolsBeforeExpensiveTurnConstruction()
    {
        Action compile = () => ToolEnvelopePlan.Compile(Tools(20));

        var exception = compile.Should().Throw<ToolEnvelopePlanException>().Which;
        exception.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == ToolEnvelopePlanDiagnosticCode.TooManyTools
            && diagnostic.Message.Contains("20 tools", StringComparison.Ordinal)
            && diagnostic.Message.Contains("limit is 16", StringComparison.Ordinal));
    }

    private static Measurement<T> Measure<T>(Func<T> action)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        var value = action();
        stopwatch.Stop();
        return new Measurement<T>(
            value,
            stopwatch.Elapsed,
            GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static ToolDefinition[] Tools(int count) =>
        Enumerable.Range(0, count)
            .Select(index => ToolDefinition.Parse(
                $"tool_{index:D2}",
                $"Processes request kind {index:D2}.",
                "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\","
                + "\"maxLength\":32}},\"required\":[\"value\"],"
                + "\"additionalProperties\":false}"))
            .ToArray();

    private sealed record Measurement<T>(T Value, TimeSpan Elapsed, long AllocatedBytes);
}

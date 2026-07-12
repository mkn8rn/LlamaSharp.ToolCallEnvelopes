using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;

namespace LlamaSharp.ToolCallEnvelopes.Tests;

[TestFixture]
public sealed class LlamaSharpToolCallRunnerTests
{
    [Test]
    public async Task RunAsync_ExecutesToolAndFeedsResultIntoFollowUpTurn()
    {
        var executor = new FakeExecutor(
            """{"tool_calls":[{"id":"call_1","name":"get_weather","arguments":{"city":"Zagreb"}}]}""",
            """{"text":"It is sunny in Zagreb."}""");
        var tools = new[] { WeatherTool() };

        var result = await LlamaSharpToolCallRunner.RunAsync(
            executor,
            [ToolAwareMessage.User("What is the weather in Zagreb?")],
            tools,
            (call, _) =>
            {
                using var args = JsonDocument.Parse(call.ArgumentsJson);
                args.RootElement.GetProperty("city").GetString().Should().Be("Zagreb");
                return Task.FromResult("{\"condition\":\"sunny\"}");
            });

        result.AssistantText.Should().Be("It is sunny in Zagreb.");
        result.Metadata.ToolCalls.Should().ContainSingle().Which.Name.Should().Be("get_weather");
        executor.Requests.Should().HaveCount(2);
        executor.Requests[0].Prompt.Messages[0].Content.Should().Contain("City to inspect");
        executor.Requests[1].Prompt.Messages.Should().Contain(message =>
            message.Content.Contains("tool_result", StringComparison.Ordinal));
        var assistantHistory = executor.Requests[1].Prompt.Messages.Single(message =>
            message.Role == ToolPromptRole.Assistant);
        assistantHistory.Content.Should().Contain("\"tool_calls\"");
        assistantHistory.Content.Should().Contain("\"arguments\"");
        assistantHistory.Content.Should().NotContain("\"mode\"");
        executor.Requests[0].Grammar.Should().Contain("inferred-tool-calls-envelope");
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task RunAsync_ForcedInitialChoiceRelaxesAfterSuccessfulToolTurn(
        bool namedChoice)
    {
        var executor = new FakeExecutor(
            """{"tool_calls":[{"id":"call_1","name":"get_weather","arguments":{"city":"Zagreb"}}]}""",
            """{"text":"It is sunny."}""");
        var tools = new[] { WeatherTool() };

        var result = await LlamaSharpToolCallRunner.RunAsync(
            executor,
            [ToolAwareMessage.User("Use the weather tool.")],
            tools,
            (_, _) => Task.FromResult("{\"condition\":\"sunny\"}"),
            new LlamaSharpToolRunOptions
            {
                ToolChoice = namedChoice
                    ? ToolChoice.ForFunction("get_weather")
                    : ToolChoice.Required,
            });

        result.AssistantText.Should().Be("It is sunny.");
        executor.Requests.Should().HaveCount(2);
        executor.Requests[0].Grammar.Split('\n')[0]
            .Should().Be("root ::= inferred-tool-calls-envelope");
        executor.Requests[1].Grammar.Split('\n')[0]
            .Should().Be("root ::= inferred-message-envelope | inferred-tool-calls-envelope");
        executor.Requests[0].Prompt.Messages[0].Content.Should().NotContain("{\"text\"");
        executor.Requests[1].Prompt.Messages[0].Content.Should().Contain("{\"text\"");
    }

    [Test]
    public async Task TryRunAsync_RepairsMalformedEnvelopeWithinConfiguredBound()
    {
        var executor = new FakeExecutor(
            "not json",
            """{"text":"Corrected answer."}""");

        var result = await LlamaSharpToolCallRunner.TryRunAsync(
            executor,
            [ToolAwareMessage.User("Answer briefly.")],
            [],
            (_, _) => Task.FromResult("unused"),
            new LlamaSharpToolRunOptions
            {
                RepairInvalidEnvelope = true,
                MaxRepairAttempts = 1,
            });

        result.Success.Should().BeTrue();
        result.Value!.AssistantText.Should().Be("Corrected answer.");
        result.Value.Metadata.RepairCount.Should().Be(1);
        executor.Requests[1].Prompt.Messages.Should().Contain(message =>
            message.Content.Contains("previous response was not a valid tool envelope", StringComparison.Ordinal));
    }

    [Test]
    public async Task TryRunAsync_ReportsRepairExhaustion()
    {
        var result = await LlamaSharpToolCallRunner.TryRunAsync(
            new FakeExecutor("not json"),
            [],
            [],
            (_, _) => Task.FromResult("unused"),
            new LlamaSharpToolRunOptions
            {
                RepairInvalidEnvelope = true,
                MaxRepairAttempts = 1,
            });

        result.Success.Should().BeFalse();
        result.Error!.Code.Should().Be("RepairExhausted");
        result.Error.RepairCount.Should().Be(1);
    }

    [Test]
    public async Task RunStreamingAsync_EmitsTextAndToolLifecycleUpdates()
    {
        var executor = new FakeExecutor("""{"text":"Hello"}""");
        var updates = new List<ToolRunUpdate>();

        await foreach (var update in LlamaSharpToolCallRunner.RunStreamingAsync(
                           executor,
                           [],
                           [],
                           (_, _) => Task.FromResult("unused")))
        {
            updates.Add(update);
        }

        string.Concat(updates.OfType<AssistantTextDelta>().Select(update => update.Text))
            .Should().Be("Hello");
        updates.OfType<ToolRunCompleted>().Should().ContainSingle();
        executor.Requests.Should().ContainSingle();
        executor.Requests[0].Grammar.Should().NotContain("inferred-tool-calls-envelope");
        executor.Requests[0].Prompt.Messages[0].Content.Should().NotContain("{\"tool_calls\"");
    }

    [Test]
    public async Task TryRunAsync_CancelsOnlyInvalidInferenceTurnOnStreamConflict()
    {
        var executor = new CancellationRecordingExecutor(
            """{"text":"partial","tool_calls":[{"id":"call_1","name":"unused","arguments":{}}]}""");
        using var callerCancellation = new CancellationTokenSource();

        var result = await LlamaSharpToolCallRunner.TryRunAsync(
            executor,
            [],
            [],
            (_, _) => Task.FromResult("unused"),
            cancellationToken: callerCancellation.Token);

        result.Success.Should().BeFalse();
        result.Error!.Code.Should().Be("PayloadConflict");
        executor.CancellationObserved.Should().BeTrue();
        callerCancellation.IsCancellationRequested.Should().BeFalse();
    }

    [Test]
    public async Task TryRunAsync_StreamRepairUsesFreshUncancelledTurnToken()
    {
        var executor = new FakeExecutor(
            """{"text":"partial","tool_calls":[{"id":"call_1","name":"unused","arguments":{}}]}""",
            """{"text":"Repaired."}""");

        var result = await LlamaSharpToolCallRunner.TryRunAsync(
            executor,
            [],
            [],
            (_, _) => Task.FromResult("unused"),
            new LlamaSharpToolRunOptions
            {
                RepairInvalidEnvelope = true,
                MaxRepairAttempts = 1,
            });

        result.Success.Should().BeTrue();
        result.Value!.AssistantText.Should().Be("Repaired.");
        result.Value.Metadata.RepairCount.Should().Be(1);
        executor.TurnTokens.Should().HaveCount(2);
        executor.TurnTokens[0].IsCancellationRequested.Should().BeTrue();
        executor.TurnTokens[1].IsCancellationRequested.Should().BeFalse();
    }

    [Test]
    public void CompleteEnvelopeGrammar_UsesWholeInferredAlternatives()
    {
        var grammar = LlamaSharpToolGrammar.BuildCompleteEnvelopeGrammar(
            [WeatherTool()],
            new ToolEnvelopeGrammarOptions
            {
                ToolChoice = ToolChoice.Auto,
                EnvelopeMode = ToolEnvelopeMode.Inferred,
                StrictTools = true,
            });

        grammar.Should().Contain("root ::= inferred-message-envelope | inferred-tool-calls-envelope");
        grammar.Should().Contain("inferred-message-envelope ::= \"{\" ws \"\\\"text\\\"\"");
        grammar.Should().Contain("inferred-tool-calls-envelope ::= \"{\" ws \"\\\"tool_calls\\\"\"");
        grammar.Should().NotContain("mode-val +");
    }

    private static ToolDefinition WeatherTool() =>
        ToolDefinition.FromJsonSchema(
            "get_weather",
            "Gets weather.",
            """
            {
              "type": "object",
              "properties": {
                "city": {
                  "type": "string",
                  "description": "City to inspect"
                }
              },
              "required": ["city"],
              "additionalProperties": false
            }
            """);

    private sealed class FakeExecutor : ILlamaSharpToolExecutor
    {
        private readonly IReadOnlyList<string> _outputs;
        private int _nextOutput;

        public ConcurrentQueue<Request> RequestQueue { get; } = new();
        public IReadOnlyList<Request> Requests => RequestQueue.ToArray();
        public ConcurrentQueue<CancellationToken> TurnTokenQueue { get; } = new();
        public IReadOnlyList<CancellationToken> TurnTokens => TurnTokenQueue.ToArray();

        public FakeExecutor(params string[] outputs) => _outputs = outputs;

        public IAsyncEnumerable<string> InferAsync(
            ToolPromptHistory prompt,
            string grammar,
            CancellationToken cancellationToken = default)
        {
            RequestQueue.Enqueue(new Request(prompt, grammar));
            TurnTokenQueue.Enqueue(cancellationToken);
            var output = _outputs[Math.Min(_nextOutput++, _outputs.Count - 1)];
            return Stream(output, cancellationToken);
        }

        private static async IAsyncEnumerable<string> Stream(
            string output,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
        {
            foreach (var character in output)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return character.ToString();
            }
        }
    }

    private sealed class CancellationRecordingExecutor(string output) : ILlamaSharpToolExecutor
    {
        public bool CancellationObserved { get; private set; }

        public async IAsyncEnumerable<string> InferAsync(
            ToolPromptHistory prompt,
            string grammar,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            using var registration = cancellationToken.Register(
                () => CancellationObserved = true);
            foreach (var character in output)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return character.ToString();
            }
        }
    }

    private sealed record Request(ToolPromptHistory Prompt, string Grammar);
}

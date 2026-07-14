namespace LlamaSharp.ToolCallEnvelopes.Tests;

[TestFixture]
[Category("ManualControl")]
public sealed class ManualControlFlowTests
{
    private const string SystemPrompt = "Use current data and return one concise answer.";

    public static IEnumerable<TestCaseData> EveryChoice
    {
        get
        {
            yield return ChoiceCase(
                ToolChoice.Auto,
                """{"text":"Automatic answer."}""",
                typeof(ToolEnvelopeOutcome.AssistantMessage));
            yield return ChoiceCase(
                ToolChoice.None,
                """{"text":"Final answer."}""",
                typeof(ToolEnvelopeOutcome.AssistantMessage));
            yield return ChoiceCase(
                ToolChoice.Required,
                TestCatalog.ToolRequest(
                    "get_weather",
                    """{"city":"Zagreb","unit":"celsius"}"""),
                typeof(ToolEnvelopeOutcome.ToolRequest));
            yield return ChoiceCase(
                ToolChoice.Named("search"),
                TestCatalog.ToolRequest("search", """{"query":"Zagreb"}"""),
                typeof(ToolEnvelopeOutcome.ToolRequest));
        }
    }

    [Test]
    public void BufferedHost_CompletesTwoToolCallsAndAFinalAnswer()
    {
        var plan = TestCatalog.Plan(maxCalls: 2);
        var conversation = new List<ToolMessage>
        {
            ToolMessage.User("Compare the weather in Zagreb and Split."),
        };
        var hostResponses = new Queue<string>(
        [
            """
            {"tool_calls":[
              {"name":"get_weather","arguments":{"city":"Zagreb","unit":"celsius"}},
              {"name":"get_weather","arguments":{"city":"Split","unit":"celsius"}}
            ]}
            """,
            """{"text":"Zagreb is 22 C and Split is 24 C."}""",
        ]);
        var observedTurns = new List<ToolEnvelopeTurn>();

        string RunExistingHostInference(ToolEnvelopeTurn turn)
        {
            turn.Prompt.Should().NotBeEmpty();
            turn.Grammar.Should().NotBeNullOrWhiteSpace();
            turn.GrammarCacheKey.Should().HaveLength(64);
            observedTurns.Add(turn);
            return hostResponses.Dequeue();
        }

        var toolTurn = plan.CreateTurn(
            SystemPrompt,
            conversation,
            ToolChoice.Required);
        var request = toolTurn.Parse(RunExistingHostInference(toolTurn))
            .Should().BeOfType<ToolEnvelopeOutcome.ToolRequest>().Which;

        conversation.Add(ToolMessage.AssistantCalls(request.Calls));
        foreach (var call in request.Calls)
            conversation.Add(ToolMessage.ToolResult(call, DispatchWeather(call)));

        var answerTurn = plan.CreateTurn(SystemPrompt, conversation, ToolChoice.None);
        var answer = answerTurn.Parse(RunExistingHostInference(answerTurn))
            .Should().BeOfType<ToolEnvelopeOutcome.AssistantMessage>().Which;
        conversation.Add(ToolMessage.Assistant(answer.Text));

        answer.Text.Should().Be("Zagreb is 22 C and Split is 24 C.");
        request.Calls.Select(call => call.Index).Should().Equal(0, 1);
        request.Calls.Select(call => call.Name).Should().OnlyContain(name => name == "get_weather");
        observedTurns.Select(turn => turn.Choice).Should().Equal(
            ToolChoice.Required,
            ToolChoice.None);
        answerTurn.Prompt.Skip(1).Should().Equal(conversation.Take(conversation.Count - 1));
        conversation.Select(message => message.Role).Should().Equal(
            ToolMessageRole.User,
            ToolMessageRole.Assistant,
            ToolMessageRole.Tool,
            ToolMessageRole.Tool,
            ToolMessageRole.Assistant);
        hostResponses.Should().BeEmpty();
    }

    [Test]
    public async Task ExistingHostStream_CompletesAToolCallAndAFinalAnswer()
    {
        var plan = TestCatalog.Plan();
        var conversation = new List<ToolMessage>
        {
            ToolMessage.User("What is the weather in Zagreb?"),
        };
        var toolTurn = plan.CreateTurn(SystemPrompt, conversation, ToolChoice.Required);
        var toolResponse = TestCatalog.ToolRequest(
            "get_weather",
            """{"city":"Zagreb","unit":"celsius"}""");

        var (toolOutcome, toolUpdates) = await ReadExistingHostStream(
            toolTurn,
            StreamExistingHostResponse(toolResponse, fragmentLength: 7));
        var request = toolOutcome.Should().BeOfType<ToolEnvelopeOutcome.ToolRequest>().Which;
        conversation.Add(ToolMessage.AssistantCalls(request.Calls));
        conversation.Add(ToolMessage.ToolResult(request.Calls[0], DispatchWeather(request.Calls[0])));

        var answerTurn = plan.CreateTurn(SystemPrompt, conversation, ToolChoice.None);
        var (answerOutcome, answerUpdates) = await ReadExistingHostStream(
            answerTurn,
            StreamExistingHostResponse("""{"text":"It is 22 C."}""", fragmentLength: 3));

        answerOutcome.Should().BeOfType<ToolEnvelopeOutcome.AssistantMessage>()
            .Which.Text.Should().Be("It is 22 C.");
        string.Concat(toolUpdates
                .OfType<ToolEnvelopeStreamUpdate.ToolArgumentsDelta>()
                .Select(update => update.Json))
            .Should().Be("""{"city":"Zagreb","unit":"celsius"}""");
        string.Concat(answerUpdates
                .OfType<ToolEnvelopeStreamUpdate.AssistantTextDelta>()
                .Select(update => update.Text))
            .Should().Be("It is 22 C.");
        answerTurn.Prompt.Skip(1).Should().Equal(conversation);
    }

    [TestCaseSource(nameof(EveryChoice))]
    public void HostCanSelectEveryTurnPolicy(
        ToolChoice choice,
        string hostOutput,
        Type expectedOutcomeType)
    {
        var plan = TestCatalog.Plan(
            tools: [TestCatalog.Weather(), TestCatalog.Search()]);
        var turn = plan.CreateTurn(
            SystemPrompt,
            [ToolMessage.User("Respond under the selected policy.")],
            choice);

        var outcome = turn.Parse(hostOutput);

        turn.Choice.Should().Be(choice);
        outcome.GetType().Should().Be(expectedOutcomeType);
    }

    [Test]
    public void HostCanAcceptARefusalWithoutDispatchingATool()
    {
        var plan = TestCatalog.Plan(allowRefusal: true);
        var turn = plan.CreateTurn(
            SystemPrompt,
            [ToolMessage.User("Decline this request.")],
            ToolChoice.None);
        var dispatchCount = 0;

        var outcome = turn.Parse("""{"refusal":"I cannot help with that."}""");
        switch (outcome)
        {
            case ToolEnvelopeOutcome.ToolRequest:
                dispatchCount++;
                break;
            case ToolEnvelopeOutcome.Refusal refusal:
                refusal.Reason.Should().Be("I cannot help with that.");
                break;
            default:
                Assert.Fail($"The refusal fixture returned unexpected outcome {outcome.GetType().Name}.");
                break;
        }

        dispatchCount.Should().Be(0);
    }

    [Test]
    public void HostOwnsBackpressureAndRetriesOnlyValidatedOutput()
    {
        var plan = TestCatalog.Plan();
        var turn = plan.CreateTurn(
            SystemPrompt,
            [ToolMessage.User("Use weather data.")],
            ToolChoice.Required);
        var hostAttempts = new Queue<string>(
        [
            """{"text":"A tool was required."}""",
            """{"tool_calls":[{"name":"get_weather","arguments":null}]}""",
            TestCatalog.ToolRequest(
                "get_weather",
                """{"city":"Zagreb","unit":"celsius"}"""),
        ]);
        var errors = new List<ToolEnvelopeError>();
        ToolEnvelopeOutcome? accepted = null;

        while (hostAttempts.TryDequeue(out var output))
        {
            if (turn.TryParse(output, out accepted, out var error))
                break;
            errors.Add(error!);
        }

        errors.Select(error => error.Code).Should().Equal(
            ToolEnvelopeErrorCode.ToolCallsRequired,
            ToolEnvelopeErrorCode.InvalidArguments);
        errors.Should().OnlyContain(error => error.Message.Contains("Recovery:", StringComparison.Ordinal));
        accepted.Should().BeOfType<ToolEnvelopeOutcome.ToolRequest>()
            .Which.Calls.Should().ContainSingle();
        hostAttempts.Should().BeEmpty();
    }

    [Test]
    public void SuiteSource_ContainsNoManagedControlDependency()
    {
        var source = File.ReadAllText(CurrentSourcePath());
        var forbiddenTypeNames = new[]
        {
            string.Concat("ILlamaSharp", "ToolExecutor"),
            string.Concat("ToolEnvelope", "Runner"),
        };

        foreach (var forbiddenTypeName in forbiddenTypeNames)
        {
            source.Should().NotContain(
                forbiddenTypeName,
                "manual control must remain usable without a managed-control dependency");
        }
    }

    private static string DispatchWeather(ToolCall call)
    {
        call.Name.Should().Be("get_weather");
        var city = call.Arguments.GetProperty("city").GetString();
        return city switch
        {
            "Zagreb" => """{"city":"Zagreb","temperature":22}""",
            "Split" => """{"city":"Split","temperature":24}""",
            _ => throw new InvalidOperationException(
                $"The manual fixture has no host result for city '{city}'."),
        };
    }

    private static async Task<(
        ToolEnvelopeOutcome Outcome,
        IReadOnlyList<ToolEnvelopeStreamUpdate> Updates)> ReadExistingHostStream(
        ToolEnvelopeTurn turn,
        IAsyncEnumerable<string> fragments)
    {
        var reader = turn.CreateStreamReader();
        var updates = new List<ToolEnvelopeStreamUpdate>();
        await foreach (var fragment in fragments)
            updates.AddRange(reader.Feed(fragment));
        return (reader.Complete(), updates);
    }

    private static async IAsyncEnumerable<string> StreamExistingHostResponse(
        string response,
        int fragmentLength)
    {
        for (var offset = 0; offset < response.Length; offset += fragmentLength)
        {
            await Task.Yield();
            yield return response.Substring(offset, Math.Min(fragmentLength, response.Length - offset));
        }
    }

    private static TestCaseData ChoiceCase(
        ToolChoice choice,
        string hostOutput,
        Type expectedOutcomeType) =>
        new TestCaseData(choice, hostOutput, expectedOutcomeType)
            .SetName($"Manual_choice_{choice}");

    private static string CurrentSourcePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var sourcePath = Path.Combine(
                directory.FullName,
                "LlamaSharp.ToolCallEnvelopes.Tests",
                "ManualControlFlowTests.cs");
            if (File.Exists(sourcePath))
                return sourcePath;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"The manual-control source guard started from '{AppContext.BaseDirectory}' but could "
            + "not find the repository checkout containing ManualControlFlowTests.cs. Run the "
            + "tests from a complete repository checkout so the dependency guard can inspect its "
            + "source.");
    }
}

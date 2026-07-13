namespace LlamaSharp.ToolCallEnvelopes.Tests;

[TestFixture]
public sealed class ManualHistoryCombinationTests
{
    private static readonly ToolChoice[] Choices =
    [
        ToolChoice.Auto,
        ToolChoice.None,
        ToolChoice.Required,
        ToolChoice.Named("get_weather"),
    ];

    public static IEnumerable<TestCaseData> EveryLiberalHistoryAndChoice
    {
        get
        {
            foreach (var sequence in HistorySequences(maximumLength: 3))
                foreach (var choice in Choices)
                {
                    var displaySequence = sequence.Length == 0 ? "empty" : sequence;
                    yield return new TestCaseData(sequence, choice)
                        .SetName($"Manual_history_{displaySequence}_choice_{Safe(choice)}");
                }
        }
    }

    public static IEnumerable<TestCaseData> EverySystemInsertionIndex =>
        Enumerable.Range(0, 5)
            .Select(index => new TestCaseData(index).SetName($"Rejects_system_at_{index}"));

    [TestCaseSource(nameof(EveryLiberalHistoryAndChoice))]
    public void EveryNonSystemHistoryOrder_RemainsAvailableToManualControl(
        string sequence,
        ToolChoice choice)
    {
        var plan = TestCatalog.Plan();
        var call = plan.CreateCall(
            0,
            "get_weather",
            TestCatalog.Json("{\"city\":\"Zagreb\",\"unit\":\"celsius\"}"));
        var messages = sequence.Select(symbol => Message(symbol, call)).ToArray();

        var turn = plan.CreateTurn("Policy.", messages, choice);

        turn.Choice.Should().Be(choice);
        turn.Prompt[0].Role.Should().Be(ToolMessageRole.System);
        turn.Prompt.Skip(1).Should().Equal(messages);
    }

    [TestCaseSource(nameof(EverySystemInsertionIndex))]
    public void SystemMessagesAreRejectedAtEveryHistoryPosition(int insertionIndex)
    {
        var plan = TestCatalog.Plan();
        var call = plan.CreateCall(
            0,
            "get_weather",
            TestCatalog.Json("{\"city\":\"Zagreb\",\"unit\":\"celsius\"}"));
        var messages = new List<ToolMessage>
        {
            ToolMessage.User("User."),
            ToolMessage.Assistant("Assistant."),
            ToolMessage.AssistantCalls([call]),
            ToolMessage.ToolResult(call, "{}"),
        };
        messages.Insert(insertionIndex, plan.CreateTurn("Old policy.", []).Prompt[0]);

        Action create = () => plan.CreateTurn("Policy.", messages);

        create.Should().Throw<ArgumentException>()
            .WithParameterName("messages")
            .WithMessage($"*message {insertionIndex} has the System role*");
    }

    [Test]
    public void GeneratedManualHistoryMatrix_CoversEveryDeclaredCombinationExactlyOnce()
    {
        var cases = EveryLiberalHistoryAndChoice.ToArray();

        cases.Should().HaveCount(340);
        cases.Select(test => test.TestName).Should().OnlyHaveUniqueItems();
    }

    private static ToolMessage Message(char symbol, ToolCall call) => symbol switch
    {
        'U' => ToolMessage.User("User message."),
        'A' => ToolMessage.Assistant("Assistant message."),
        'C' => ToolMessage.AssistantCalls([call]),
        'R' => ToolMessage.ToolResult(call, "{}"),
        _ => throw new ArgumentOutOfRangeException(
            nameof(symbol),
            symbol,
            "The manual-history matrix contains an unknown message symbol."),
    };

    private static IEnumerable<string> HistorySequences(int maximumLength)
    {
        yield return string.Empty;
        var current = new List<string> { string.Empty };
        for (var length = 1; length <= maximumLength; length++)
        {
            current = current
                .SelectMany(prefix => new[] { 'U', 'A', 'C', 'R' }
                    .Select(symbol => prefix + symbol))
                .ToList();
            foreach (var sequence in current)
                yield return sequence;
        }
    }

    private static string Safe(ToolChoice choice) =>
        choice.ToString().Replace('(', '_').Replace(")", string.Empty);
}

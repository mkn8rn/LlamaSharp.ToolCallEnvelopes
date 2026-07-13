namespace LlamaSharp.ToolCallEnvelopes.Tests;

[TestFixture]
public sealed class ManagedPartialBatchFailureTests
{
    public static IEnumerable<TestCaseData> EveryFailureAtEveryCall
    {
        get
        {
            foreach (var failure in Enum.GetValues<DispatchFailure>())
            {
                for (var callIndex = 0; callIndex < 3; callIndex++)
                {
                    yield return new TestCaseData(failure, callIndex)
                        .SetName($"{failure}_at_call_{callIndex}");
                }
            }
        }
    }

    [TestCaseSource(nameof(EveryFailureAtEveryCall))]
    public async Task FailureAtEveryBatchPosition_PreservesOnlyCommittedResults(
        DispatchFailure failure,
        int failingIndex)
    {
        var plan = TestCatalog.Plan(
            maxCalls: 3,
            limits: ToolEnvelopeLimits.Constrained with { MaxToolResultCharacters = 8 });
        var request = ThreeCallRequest();
        var attempted = new List<int>();

        var result = await ToolEnvelopeRunner.RunAsync(
            new QueueExecutor(request),
            plan,
            "Policy.",
            [ToolMessage.User("Request.")],
            (call, _) =>
            {
                attempted.Add(call.Index);
                if (call.Index != failingIndex)
                    return ValueTask.FromResult($"ok-{call.Index}");

                return failure switch
                {
                    DispatchFailure.Throws => throw new TestAdapterException("dispatch"),
                    DispatchFailure.Null => ValueTask.FromResult<string>(null!),
                    DispatchFailure.TooLarge => ValueTask.FromResult("123456789"),
                    _ => throw new ArgumentOutOfRangeException(nameof(failure)),
                };
            },
            new ToolRunOptions
            {
                InitialChoice = ToolChoice.Required,
                MaxModelTurns = 1,
            });

        var failed = result.Should().BeOfType<ToolRunResult.Failed>().Which;
        failed.Failure.Code.Should().Be(failure switch
        {
            DispatchFailure.Throws => ToolRunFailureCode.ToolExecutionFailed,
            DispatchFailure.Null => ToolRunFailureCode.ToolResultWasNull,
            DispatchFailure.TooLarge => ToolRunFailureCode.ToolResultTooLarge,
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        });
        attempted.Should().Equal(Enumerable.Range(0, failingIndex + 1));
        failed.Executions.Select(execution => execution.Call.Index)
            .Should().Equal(Enumerable.Range(0, failingIndex));

        failed.Conversation.Should().HaveCount(2 + failingIndex);
        failed.Conversation[0].Role.Should().Be(ToolMessageRole.User);
        failed.Conversation[1].Role.Should().Be(ToolMessageRole.Assistant);
        failed.Conversation[1].Calls.Select(call => call.Index).Should().Equal(0, 1, 2);
        failed.Conversation.Skip(2).Select(message => message.AnsweredCall!.Index)
            .Should().Equal(Enumerable.Range(0, failingIndex));
    }

    [Test]
    public void PartialBatchFailureMatrix_CoversEveryFailureAndCallPosition()
    {
        EveryFailureAtEveryCall.Should().HaveCount(9);
    }

    private static string ThreeCallRequest() =>
        """
        {"tool_calls":[
          {"name":"get_weather","arguments":{"city":"Zagreb","unit":"celsius"}},
          {"name":"get_weather","arguments":{"city":"Split","unit":"celsius"}},
          {"name":"get_weather","arguments":{"city":"Rijeka","unit":"celsius"}}
        ]}
        """;

    public enum DispatchFailure
    {
        Throws,
        Null,
        TooLarge,
    }
}

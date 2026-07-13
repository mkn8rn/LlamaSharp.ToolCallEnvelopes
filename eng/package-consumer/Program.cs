using System.Runtime.CompilerServices;
using LlamaSharp.ToolCallEnvelopes;

var tool = ToolDefinition.Parse(
    "echo",
    "Returns one validated value.",
    """
    {
      "type": "object",
      "properties": {
        "value": { "type": "string", "const": "ok" }
      },
      "required": ["value"],
      "additionalProperties": false
    }
    """);
var plan = ToolEnvelopePlan.Compile([tool]);
var history = new List<ToolMessage> { ToolMessage.User("Call echo.") };
var requestTurn = plan.CreateTurn("Use the requested tool.", history, ToolChoice.Required);
var request = requestTurn.Parse(
    "{\"tool_calls\":[{\"name\":\"echo\",\"arguments\":{\"value\":\"ok\"}}]}")
    as ToolEnvelopeOutcome.ToolRequest
    ?? throw new InvalidOperationException(
        "The package consumer expected a validated ToolRequest from a Required turn. Recheck the "
        + "packed parser, policy, and schema assets before publication.");

history.Add(ToolMessage.AssistantCalls(request.Calls));
history.Add(ToolMessage.ToolResult(request.Calls[0], "{\"echo\":\"ok\"}"));
var answerTurn = plan.CreateTurn("Answer from the tool result.", history, ToolChoice.None);
var answer = answerTurn.Parse("{\"text\":\"done\"}")
    as ToolEnvelopeOutcome.AssistantMessage
    ?? throw new InvalidOperationException(
        "The package consumer expected an AssistantMessage from a None turn. Recheck the packed "
        + "prompt, grammar, and parser contract before publication.");

var managedResult = await ToolEnvelopeRunner.RunAsync(
    new StaticExecutor(
        "{\"tool_calls\":[{\"name\":\"echo\",\"arguments\":{\"value\":\"ok\"}}]}",
        "{\"text\":\"managed done\"}"),
    plan,
    "Use echo, then answer from its result.",
    [ToolMessage.User("Call echo through managed control flow.")],
    (call, cancellationToken) =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult("{\"echo\":\"ok\"}");
    },
    new ToolRunOptions
    {
        InitialChoice = ToolChoice.Required,
        FollowUpChoice = ToolChoice.None,
        MaxModelTurns = 2,
    });
var managed = managedResult as ToolRunResult.Completed
    ?? throw new InvalidOperationException(
        "The packed managed runner did not complete a Required-to-None flow. Recheck its retry, "
        + "dispatch, history, and completion pipeline before publication.");
var managedAnswer = managed.Outcome as ToolEnvelopeOutcome.AssistantMessage
    ?? throw new InvalidOperationException(
        "The packed managed runner completed without an AssistantMessage. Recheck its final "
        + "outcome contract before publication.");
if (managed.Executions.Count != 1 || managedAnswer.Text != "managed done")
{
    throw new InvalidOperationException(
        $"The packed managed runner reported {managed.Executions.Count} execution(s) and answer "
        + $"'{managedAnswer.Text}', expected one execution and 'managed done'. Recheck the "
        + "managed dispatch and completion pipeline before publication.");
}

Console.WriteLine(
    $"PACKAGE_CONSUMER_OK manual_calls={request.Calls.Count} manual_answer={answer.Text} "
    + $"managed_calls={managed.Executions.Count} managed_answer={managedAnswer.Text} "
    + $"framework={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");

internal sealed class StaticExecutor(params string[] outputs) : ILlamaSharpToolExecutor
{
    private readonly Queue<string> _outputs = new(outputs);

    public async IAsyncEnumerable<string> InferAsync(
        ToolEnvelopeTurn turn,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_outputs.Count == 0)
        {
            throw new InvalidOperationException(
                $"The isolated package consumer has no model output for turn choice "
                + $"'{turn.Choice}'. Add the missing deterministic output before trusting this "
                + "consumer test.");
        }

        yield return _outputs.Dequeue();
        await Task.CompletedTask;
    }
}

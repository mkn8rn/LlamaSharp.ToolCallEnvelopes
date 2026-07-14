# Manual control

Manual control requires neither `ILlamaSharpToolExecutor` nor
`ToolEnvelopeRunner`. The application keeps its existing `ChatSession`,
`ChatAsync`, `InferAsync`, or other inference code. TCE creates one exact turn,
then parses the string or fragments that application code produces.

The plan owns the tool contract, prompt messages, GBNF grammar, parser, and
argument validation. The application owns inference, retries, dispatch,
authorization, history storage, persistence, context reuse, and the decision
to create another turn.

## Create one turn

Compile the plan once, keep conversation state as `ToolMessage` values, and
create the policy required for the next model response.

```csharp
var conversation = new List<ToolMessage>
{
    ToolMessage.User("Compare the weather in Zagreb and Split."),
};

var turn = plan.CreateTurn(
    "Use current weather data.",
    conversation,
    ToolChoice.Required);
```

`turn.Prompt` contains the complete role-and-content messages for this turn.
Map them through the GGUF model's native chat template. Attach `turn.Grammar`
with start rule `root`. Those operations happen inside the application's
existing inference path; manual control does not require a TCE adapter.

`turn.GrammarCacheKey` identifies the plan and choice used by this grammar, so
the host may cache its native grammar object. `turn.Metrics` reports prompt and
grammar character counts for host-side context budgeting.

## Parse the existing model output

After the application's existing inference code has produced the response,
parse it with the same turn that supplied the prompt and grammar.

```csharp
string output = outputBuilder.ToString().Trim();
ToolEnvelopeOutcome outcome = turn.Parse(output);
```

There is no executor argument here. TCE does not know whether `output` came
from `ChatSession`, `ChatAsync`, `InferAsync`, another model API, a stored
response, or a test. It validates only whether the complete response obeys
this turn's compiled contract.

Use `TryParse` when the application already owns retry or backpressure logic.

```csharp
if (!turn.TryParse(output, out var outcome, out var error))
{
    logger.LogWarning(
        "Rejected {Code} at {Pointer}: {Preview}",
        error.Code,
        error.JsonPointer,
        error.PayloadPreview);

    return await RetryWithHostPolicyAsync(error, cancellationToken);
}
```

`Parse` throws `ToolEnvelopeException` for rejected model output. `TryParse`
returns `false` with one bounded `ToolEnvelopeError`. Null input and invalid API
usage throw normal argument exceptions. A rejected call is never safe to
dispatch.

## Handle the outcome yourself

The parsed outcome is a closed set. The application decides what each outcome
means for its own control flow.

```csharp
switch (outcome)
{
    case ToolEnvelopeOutcome.AssistantMessage answer:
        conversation.Add(ToolMessage.Assistant(answer.Text));
        return answer.Text;

    case ToolEnvelopeOutcome.Refusal refusal:
        return refusal.Reason;

    case ToolEnvelopeOutcome.ToolRequest request:
        conversation.Add(ToolMessage.AssistantCalls(request.Calls));

        foreach (var call in request.Calls)
        {
            Authorize(call);
            var result = await DispatchAsync(call, cancellationToken);
            conversation.Add(ToolMessage.ToolResult(call, result));
        }

        break;
}
```

TCE has validated the call name and arguments against the compiled catalog.
The application must still authorize the operation and enforce business rules
before performing a side effect.

## Create the next turn explicitly

After a tool result, the application chooses whether another model response is
needed. The following turn requires a final answer and prohibits another tool
request.

```csharp
var answerTurn = plan.CreateTurn(
    "Use current weather data.",
    conversation,
    ToolChoice.None);

string answerOutput = answerOutputBuilder.ToString().Trim();
var finalOutcome = answerTurn.Parse(answerOutput);
```

The host may instead create an Auto, Required, or Named turn, pause for human
approval, persist the conversation, retry with different sampling, or stop.
Manual control contains no hidden state machine.

When a persisted call is loaded later, recreate it through the same plan
before placing it back into history.

```csharp
var call = plan.CreateCall(
    storedIndex,
    storedName,
    storedArguments);
```

This revalidates the stored name and arguments against the current catalog and
schema.

## Feed an existing response stream

When the application already receives response fragments, pass those fragments
to a reader created by the same turn. The stream may come from any inference
API.

```csharp
var reader = turn.CreateStreamReader();
IAsyncEnumerable<string> fragments = existingModelStream;

await foreach (var fragment in fragments.WithCancellation(cancellationToken))
{
    foreach (var update in reader.Feed(fragment))
    {
        if (update is ToolEnvelopeStreamUpdate.AssistantTextDelta text)
            Console.Write(text.Text);
    }
}

var streamedOutcome = reader.Complete();
```

Stream updates are provisional. `Complete` performs the same authoritative
validation as `turn.Parse`. A reader is single-use, and a rejected or completed
reader cannot accept more fragments.

## Keep the manual boundary

Manual control is for applications that want to decide retries, parallel or
transactional dispatch, dependency ordering, approval, persistence, context
reuse, and resumability themselves. Implement `ILlamaSharpToolExecutor` only
when the application deliberately chooses the managed runner instead.

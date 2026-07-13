# Manual control

Manual control is for applications that already have an orchestration model or
need policies that a general runner should not guess. The plan still owns
schema compilation, prompt semantics, GBNF, parsing, and validation. The host
owns inference, retry decisions, dispatch ordering, approvals, persistence,
context reuse, and the next tool choice.

## Create one exact turn

Keep conversation state as `ToolMessage` values and ask the plan for the policy
needed now.

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

Map every `turn.Prompt` role and content through the model's native chat
template. Attach `turn.Grammar` with start rule `root`. `turn.GrammarCacheKey`
is stable for the plan and choice, so the adapter may cache the native grammar
object. `turn.Metrics` reports role-content and grammar character counts for
host-side context budgeting.

## Parse a complete response

`Parse` is concise when invalid model output should be exceptional.

```csharp
var raw = await RunModelAsync(turn, cancellationToken);
var outcome = turn.Parse(raw);
```

`TryParse` is convenient when the host has its own retry policy.

```csharp
if (!turn.TryParse(raw, out var outcome, out var error))
{
    logger.LogWarning(
        "Rejected {Code} at {Pointer}: {Preview}",
        error.Code,
        error.JsonPointer,
        error.PayloadPreview);

    return await RetryWithHostPolicyAsync(error, cancellationToken);
}
```

Null input and invalid API usage throw normal argument exceptions. Rejected
model output returns `false` with one bounded `ToolEnvelopeError`.

## Own the tool loop

The accepted outcome is a closed set.

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

The host now decides what comes next. It may create an Auto turn, force a final
answer with None, require another named tool, pause for approval, store the
conversation, or stop. The package does not create a hidden state machine
around a manual turn.

When persisted tool calls are loaded later, recreate them through the same
plan. This validates the name and arguments before they enter a prompt.

```csharp
var call = plan.CreateCall(
    storedIndex,
    storedName,
    storedArguments);
```

## Stream one turn

The stream reader holds one bounded raw response and incrementally publishes
decoded answer text, decoded refusal text, or raw argument JSON fragments.

```csharp
var reader = turn.CreateStreamReader();

await foreach (var fragment in executor.InferAsync(turn, cancellationToken))
{
    foreach (var update in reader.Feed(fragment))
    {
        if (update is ToolEnvelopeStreamUpdate.AssistantTextDelta text)
            Console.Write(text.Text);
    }
}

var accepted = reader.Complete();
```

Updates are provisional. Noncanonical but valid property ordering can complete
successfully without producing argument deltas. Completion performs the same
authoritative validation as `turn.Parse`. A reader is single-use, and a
rejected or completed reader cannot accept more fragments.

## Keep the host boundary explicit

Manual control permits policies that are intentionally absent from the managed
runner, including parallel dispatch, dependency graphs, transaction scopes,
human approval, domain-specific repair prompts, per-tool retry rules, and
durable resumability. Those policies belong in application code because their
correct behavior depends on side effects and product semantics.

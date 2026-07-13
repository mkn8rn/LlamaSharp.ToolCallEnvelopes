# Getting started

Managed control flow is the default route when the application wants the
package to run the ordinary answer-or-tool loop. The application supplies one
compiled plan, one LlamaSharp executor, the conversation, and a dispatcher.
The runner supplies the bounded control flow around them.

The project must target .NET 10.

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
</PropertyGroup>
```

Install the package from NuGet.org.

```powershell
dotnet add package Supprocom.LlamaSharp.ToolCallEnvelopes --version 0.2.0
```

## Compile the tool plan once

A tool schema is application configuration, not model output. Compile it when
the application builds its tool catalog and reuse the resulting plan. Plan
compilation validates names, closes every object, resolves local references,
checks the resource limits, creates each tool-choice grammar, and builds a
stable cache key.

```csharp
var weather = ToolDefinition.Parse(
    "get_weather",
    "Gets the current weather for one city.",
    """
    {
      "type": "object",
      "properties": {
        "city": {
          "type": "string",
          "minLength": 1,
          "maxLength": 64,
          "description": "City name, for example Zagreb"
        },
        "unit": {
          "type": "string",
          "enum": ["celsius", "fahrenheit"],
          "description": "Temperature unit"
        }
      },
      "required": ["city", "unit"],
      "additionalProperties": false
    }
    """);

var plan = ToolEnvelopePlan.Compile([weather]);
```

`ToolEnvelopePlanException.Diagnostics` reports every problem found in the
compilation pass with a typed code, tool name, and JSON Pointer. The compiler
does not replace an unsupported schema with a permissive grammar.

## Connect the model

The executor receives a complete `ToolEnvelopeTurn`. Apply the GGUF model's
native chat template to `turn.Prompt`, set `AddAssistant` to `true`, attach a
LlamaSharp `Grammar` built from `turn.Grammar` with start rule `root`, and yield
only the new response fragments.

This boundary deliberately leaves context size, KV-cache reuse, sampling,
threads, GPU layers, and cancellation with the host. A compact LlamaSharp
implementation is shown in
[the adapter guide](llamasharp-adapter.md), and the repository
[demo](../.Demo/Program.cs) is directly buildable.

## Run the managed loop

The dispatcher receives only catalog-known, schema-valid calls. It still owns
authorization and business validation because a structurally valid call is not
permission to perform a side effect.

```csharp
var result = await ToolEnvelopeRunner.RunAsync(
    executor,
    plan,
    "Use current weather data when a question requires it.",
    [ToolMessage.User("What is the weather in Zagreb?")],
    async (call, cancellationToken) =>
    {
        var city = call.Arguments.GetProperty("city").GetString()!;
        var unit = call.Arguments.GetProperty("unit").GetString()!;

        return await weatherService.GetJsonAsync(
            city,
            unit,
            cancellationToken);
    },
    cancellationToken: cancellationToken);
```

The defaults allow the first turn to answer or call a tool, allow the same
choice after tool results, permit four accepted model turns, and permit two
generation attempts per turn. Invalid output consumes an attempt rather than a
model turn. Tool calls are always dispatched sequentially.

For a route that must call one tool and then must answer, state that policy in
the options. The grammar and parser enforce it even if an adapter ignores the
grammar.

```csharp
var options = new ToolRunOptions
{
    InitialChoice = ToolChoice.Named("get_weather"),
    FollowUpChoice = ToolChoice.None,
    MaxModelTurns = 2,
    MaxAttemptsPerTurn = 2,
};
```

`ToolChoice.Auto` allows a final answer or an available tool request.
`ToolChoice.None` allows a final answer and no tool request.
`ToolChoice.Required` requires at least one available tool request.
`ToolChoice.Named("get_weather")` requires that exact catalog tool. A refusal
is a separate plan capability and is never legal on a required or named turn.

## Read the result

A completed managed run contains only a final answer or refusal. An
intermediate tool request cannot appear as a completed result.

```csharp
switch (result)
{
    case ToolRunResult.Completed
    {
        Outcome: ToolEnvelopeOutcome.AssistantMessage answer
    }:
        Console.WriteLine(answer.Text);
        break;

    case ToolRunResult.Completed
    {
        Outcome: ToolEnvelopeOutcome.Refusal refusal
    }:
        Console.WriteLine(refusal.Reason);
        break;

    case ToolRunResult.Failed failed:
        Console.Error.WriteLine(
            failed.Failure.Code + ": " + failed.Failure.Message);
        break;
}
```

Every result includes an immutable conversation snapshot and the successful
tool executions in order. A failure also identifies its model turn, attempt,
elapsed time, optional envelope error, and optional executor, dispatcher, or
observer exception. Caller cancellation remains cancellation and throws
`OperationCanceledException`.

## Observe provisional output

An observer receives ordered attempt, provisional stream, validation, and
dispatch events. Text, refusal, and argument deltas are provisional until the
matching `AttemptAccepted` event arrives.

```csharp
var options = new ToolRunOptions
{
    Observer = (update, cancellationToken) =>
    {
        if (update is ToolRunEvent.AssistantTextDelta delta)
            Console.Write(delta.Text);

        return ValueTask.CompletedTask;
    },
};
```

If the application needs different retry rules, parallel or transactional
dispatch, approval gates, resumable state, or a custom turn state machine,
continue with [manual control](manual-control.md).

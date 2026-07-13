# LlamaSharp.ToolCallEnvelopes

LlamaSharp.ToolCallEnvelopes gives a local LlamaSharp model one small JSON
language for final answers, tool requests, and optional refusals. A compiled
plan keeps the prompt, GBNF grammar, parser, and argument validator together, so
the model cannot be prompted for one shape and parsed as another.

The package targets .NET 10 and leaves model loading, sampling, native context
ownership, and chat-template selection in your LlamaSharp adapter.

## Install

```powershell
dotnet add package Supprocom.LlamaSharp.ToolCallEnvelopes --version 0.2.0
```

## Managed control flow

Managed control flow is the short path for an application that wants a useful
tool loop today. The runner creates turns, retries invalid model output,
validates every call before dispatch, executes calls sequentially, appends tool
results, and stops on a final answer or refusal.

```csharp
var weather = ToolDefinition.Parse(
    "get_weather",
    "Gets the current weather for one city.",
    """
    {
      "type": "object",
      "properties": {
        "city": { "type": "string", "minLength": 1, "maxLength": 64 },
        "unit": { "type": "string", "enum": ["celsius", "fahrenheit"] }
      },
      "required": ["city", "unit"],
      "additionalProperties": false
    }
    """);

var plan = ToolEnvelopePlan.Compile([weather]);

var result = await ToolEnvelopeRunner.RunAsync(
    executor,
    plan,
    "Use current weather data when the user asks about weather.",
    [ToolMessage.User("What is the weather in Zagreb?")],
    async (call, cancellationToken) =>
    {
        var city = call.Arguments.GetProperty("city").GetString();
        return await GetWeatherJsonAsync(city!, cancellationToken);
    },
    cancellationToken: cancellationToken);

if (result is ToolRunResult.Completed
    {
        Outcome: ToolEnvelopeOutcome.AssistantMessage answer
    })
{
    Console.WriteLine(answer.Text);
}
```

The `executor` implements `ILlamaSharpToolExecutor`. It applies the model's
native chat template to `turn.Prompt`, attaches `turn.Grammar` with the `root`
start rule, and yields only newly generated text. The complete working
LlamaSharp adapter is in
[the demo](https://github.com/Supprocom/LlamaSharp.ToolCallEnvelopes/blob/main/.Demo/Program.cs).

## Manual control flow

Manual control flow exposes the same compiled turn without choosing retries,
dispatch policy, history storage, context reuse, or when the next turn should
start. This is the open route for applications with their own orchestration.

```csharp
var conversation = new List<ToolMessage>
{
    ToolMessage.User("What is the weather in Zagreb?"),
};

var turn = plan.CreateTurn(
    "Use current weather data.",
    conversation,
    ToolChoice.Auto);

var reader = turn.CreateStreamReader();
await foreach (var fragment in executor.InferAsync(turn, cancellationToken))
    reader.Feed(fragment);

var outcome = reader.Complete();
```

Read [getting started](https://github.com/Supprocom/LlamaSharp.ToolCallEnvelopes/blob/main/docs/getting-started.md)
for the managed path, [manual control](https://github.com/Supprocom/LlamaSharp.ToolCallEnvelopes/blob/main/docs/manual-control.md)
for host-owned orchestration, and [the schema profile](https://github.com/Supprocom/LlamaSharp.ToolCallEnvelopes/blob/main/docs/schema-profile.md)
for the exact accepted JSON Schema subset.

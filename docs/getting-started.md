# Getting started with LlamaSharp tool-call envelopes

## Install and use

Install the package from
[NuGet: Supprocom.LlamaSharp.ToolCallEnvelopes](https://www.nuget.org/packages/Supprocom.LlamaSharp.ToolCallEnvelopes).
This guide then walks from package installation through a runnable demo, tool
definition, prompt formatting, grammar construction, model output parsing,
tool execution, and the follow-up answer turn.

This package gives a LlamaSharp application a constrained JSON envelope for tool
calling. The normal inferred envelope uses `text`, `tool_calls`, or `refusal` as
the payload discriminator, while strict-declared mode remains available for
hosts that need the original `mode`, `text`, and `calls` shape. It does not load
models, run inference, execute tools, persist conversation state, or own your
application protocol. Its job is narrower: it formats tool-aware prompt
history, builds the GBNF grammar that constrains local model output, parses the
final envelope, and optionally walks streaming output into text or tool-call
deltas while generation is still in progress.

The repository includes a runnable `.Demo` console project that shows the full
flow against a real local GGUF model. The demo downloads Qwen2.5 0.5B Instruct
Q4_0 into its assembly directory when the file is missing, defines a weather
tool schema, forces one tool call, dispatches a hardcoded tool result, runs the
follow-up answer turn, and then runs a refusal-capable turn. For example:

```powershell
dotnet run --project .\.Demo\.Demo.csproj -c Release
```

The first run downloads the model to `.Demo/bin/Release/net10.0/`, which is
ignored by the repository's existing `bin/` rule. Later runs reuse the file when
the expected byte length is already present. The demo is intentionally
hardcoded so you can inspect one complete code path in
[`../.Demo/Program.cs`](../.Demo/Program.cs) without first building an
application framework around it. Its first turn uses
`ToolChoice.ForFunction("get_weather")` so the demo always exercises the
tool-call path. That is demonstration forcing, not a requirement for normal
applications. If a real assistant turn should be allowed to either answer in
plain text or call a tool, use `ToolChoice.Auto` for that turn instead.

After installing `LlamaSharp.ToolCallEnvelopes`, define your tool catalog. A
tool has a stable name, a description for the model, and a JSON Schema object
that describes the argument object the model must produce. The schema is a
`JsonElement`, so clone it from a `JsonDocument` if the document will be
disposed. For example:

```csharp
using System.Text.Json;
using LlamaSharp.ToolCallEnvelopes;

using var schemaDocument = JsonDocument.Parse("""
{
  "type": "object",
  "properties": {
    "city": {
      "type": "string",
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

var tools = new[]
{
    new ToolDefinition(
        "get_weather",
        "Gets the current weather for a city.",
        schemaDocument.RootElement.Clone())
};
```

## Choose control-flow ownership

There are two valid ways to use the package. In the unmanaged style, your
application owns the local control flow. It builds the prompt and grammar,
starts LlamaSharp, parses the completed envelope, authorizes and dispatches
tool calls, appends the assistant call and tool result messages, and decides
when to run the next model turn. This is the appropriate style when the
application needs custom turn selection, persistence, authorization, retries,
parallel dispatch, UI state transitions, or other control-flow rules that the
package cannot know about. The unmanaged flow uses the low-level APIs directly:

```csharp
var promptHistory = LlamaSharpToolPromptBuilder.Build(
    systemPrompt: "Use tools when they are needed.",
    messages: conversation,
    tools: tools,
    options: new ToolPromptOptions
    {
        ToolChoice = ToolChoice.Auto,
        EnvelopeMode = ToolEnvelopeMode.Inferred,
        StrictTools = true,
    });

var grammar = LlamaSharpToolGrammar.BuildCompleteEnvelopeGrammar(
    tools,
    new ToolEnvelopeGrammarOptions
    {
        ToolChoice = ToolChoice.Auto,
        EnvelopeMode = ToolEnvelopeMode.Inferred,
        StrictTools = true,
    });

var rawModelOutput = await RunOneLlamaSharpTurnAsync(
    promptHistory,
    grammar,
    cancellationToken);
var envelope = LlamaSharpToolEnvelopeParser.Parse(rawModelOutput);

if (envelope.HasToolCalls)
{
    conversation.Add(ToolAwareMessage.AssistantWithToolCalls(
        envelope.ToolCalls,
        envelope.Content));

    foreach (var call in envelope.ToolCalls)
    {
        var toolResult = await DispatchToolAsync(call, cancellationToken);
        conversation.Add(ToolAwareMessage.ToolResult(call.Id, toolResult));
    }
}
```

`RunOneLlamaSharpTurnAsync` in this example is application code. It represents
the host-specific LlamaSharp session, prompt rendering, sampling pipeline, and
generated-text buffering shown in the later sections of this guide. The
package intentionally does not provide that model-session method.

The managed style delegates the repeated envelope loop to
`LlamaSharpToolCallRunner`. The host still supplies the LlamaSharp adapter and
the tool-dispatch callback, but the runner handles prompt construction,
grammar construction, stream validation, final parsing, tool-result history,
bounded repair, and the decision to continue until a final answer or refusal.
It is a convenience orchestration layer and a practical application template,
not an objectively best integration for every application. Use it when the
default loop is close to what the application needs and you want to avoid
reimplementing the ordinary tool-call turns. Use the unmanaged style when the
application needs to own those decisions itself.

The managed call site looks like this; the complete adapter implementation is
shown below:

```csharp
var managedResult = await LlamaSharpToolCallRunner.RunAsync(
    executor,
    conversation,
    tools,
    DispatchToolAsync,
    new LlamaSharpToolRunOptions
    {
        ToolChoice = ToolChoice.Auto,
        EnvelopeMode = ToolEnvelopeMode.Inferred,
        StreamValidation = ToolEnvelopeStreamValidation.Strict,
        StrictTools = true,
        RepairInvalidEnvelope = true,
        MaxRepairAttempts = 1,
        MaxTurns = 4,
        SystemPrompt = "Use tools when the question requires them.",
    },
    cancellationToken);

Console.WriteLine(managedResult.AssistantText);
```

For each model turn, build prompt history from your own conversation messages.
Plain user messages pass through. Plain assistant messages are wrapped back into
the envelope shape so the model sees a consistent transcript. Previous
assistant tool calls and tool results are also formatted back into the structure
expected by the next local inference turn. Tool-call arguments in history must
already be valid JSON objects; the builder rejects empty strings, malformed
JSON, arrays, strings, and other non-object values because silently replacing
bad history with `{}` would lose tool execution context. For example:

```csharp
var conversation = new List<ToolAwareMessage>
{
    ToolAwareMessage.User(
        "What is the current weather in Zagreb? Use metric units and call the weather tool.")
};

var promptHistory = LlamaSharpToolPromptBuilder.Build(
    systemPrompt: "You are concise and use tools when they are needed.",
    messages: conversation,
    tools: tools,
    options: new ToolPromptOptions
    {
        ToolChoice = ToolChoice.Auto,
        EnvelopeMode = ToolEnvelopeMode.Inferred,
        StrictTools = true,
    });
```

`ToolPromptHistory` deliberately uses package-local roles instead of depending
on LlamaSharp runtime types. If your application uses `ChatHistory`, map the
package roles into `AuthorRole`. For example:

```csharp
using LLama.Common;

var chatHistory = new ChatHistory();

foreach (var message in promptHistory.Messages)
{
    var role = message.Role switch
    {
        ToolPromptRole.System => AuthorRole.System,
        ToolPromptRole.User => AuthorRole.User,
        ToolPromptRole.Assistant => AuthorRole.Assistant,
        _ => throw new InvalidOperationException(
            $"Unsupported prompt role '{message.Role}'.")
    };

    chatHistory.AddMessage(role, message.Content);
}
```

The demo uses an even smaller raw prompt renderer because the package contract
is just role plus content. That keeps the example independent of any one
LlamaSharp chat-template path. For example:

```csharp
static string RenderPrompt(ToolPromptHistory promptHistory)
{
    var sb = new StringBuilder();
    foreach (var message in promptHistory.Messages)
    {
        sb.Append("### ");
        sb.AppendLine(message.Role switch
        {
            ToolPromptRole.System => "System",
            ToolPromptRole.User => "User",
            ToolPromptRole.Assistant => "Assistant",
            _ => throw new InvalidOperationException(
                $"Unsupported prompt role '{message.Role}'.")
        });
        sb.AppendLine(message.Content);
        sb.AppendLine();
    }

    sb.AppendLine("### Assistant");
    return sb.ToString();
}
```

Build the grammar for the same tool catalog and tool-choice policy that you used
for the prompt. `ToolChoice.Auto` lets the model either answer directly or call
tools. `ToolChoice.None` disables tool calls. `ToolChoice.Required` forces at
least one tool call. `ToolChoice.ForFunction("name")` pins the grammar to one
named tool and a single call. `parallelCalls: false` restricts the calls array
to a single tool call except where the selected choice is already single-call.
For example:

```csharp
var autoGrammarText = LlamaSharpToolGrammar.BuildCompleteEnvelopeGrammar(
    tools,
    new ToolEnvelopeGrammarOptions
    {
        ToolChoice = ToolChoice.Auto,
        EnvelopeMode = ToolEnvelopeMode.Inferred,
        ParallelToolCalls = false,
        StrictTools = true,
    });
```

Use `ToolChoice.Auto` for the common case where the model may answer directly
or call a tool depending on the prompt. The demo uses `ToolChoice.ForFunction`
only to make the sample reliably demonstrate the tool-call branch. For example:

```csharp
var grammarText = LlamaSharpToolGrammar.BuildCompleteEnvelopeGrammar(
    tools,
    new ToolEnvelopeGrammarOptions
    {
        ToolChoice = ToolChoice.ForFunction("get_weather"),
        EnvelopeMode = ToolEnvelopeMode.Inferred,
        ParallelToolCalls = false,
        StrictTools = true,
    });
```

In strict mode, every supplied tool schema must be convertible into GBNF without
unsupported or relaxed features. If a schema uses a construct the converter
cannot enforce, `LlamaSharpToolSchemaException` is thrown before inference. That
is intentional. Strict mode means the generated grammar actually enforces the
schema. If your application wants a looser argument object, either simplify the
schema or call the non-strict grammar path by passing `strict: false`. For
example:

```csharp
try
{
    var strictGrammar = LlamaSharpToolGrammar.BuildCompleteEnvelopeGrammar(
        tools,
        new ToolEnvelopeGrammarOptions
        {
            ToolChoice = ToolChoice.Auto,
            EnvelopeMode = ToolEnvelopeMode.Inferred,
            StrictTools = true,
        });
}
catch (LlamaSharpToolSchemaException ex)
{
    Console.Error.WriteLine(
        $"Tool schema for '{ex.ToolName}' cannot be enforced by the grammar.");
}
```

Attach the grammar to LlamaSharp generation using root rule `root`. In
LLamaSharp `0.27.0`, the grammar type used by `DefaultSamplingPipeline` is
`LLama.Sampling.Grammar`. For example:

```csharp
using LLama.Sampling;

var pipeline = new DefaultSamplingPipeline
{
    Grammar = new Grammar(grammarText, "root"),
    Temperature = 0.1f,
    TopP = 0.9f,
    Seed = 42
};
```

Your host application still loads the model and owns the executor. The demo
uses the CPU backend and a Qwen2.5 0.5B GGUF. For example:

```csharp
using LLama;
using LLama.Common;

var modelParams = new ModelParams(modelPath)
{
    ContextSize = 2048,
    BatchSize = 128,
    GpuLayerCount = 0,
    Threads = Math.Max(1, Environment.ProcessorCount / 2)
};

using var weights = await LLamaWeights.LoadFromFileAsync(
    modelParams,
    cancellationToken,
    null);

using var context = weights.CreateContext(modelParams);
var executor = new InteractiveExecutor(context);
```

Once you have a rendered prompt and a grammar-backed sampling pipeline, run
inference and buffer the generated text. The completed raw output should be the
JSON envelope. For example:

```csharp
var inferenceParams = new InferenceParams
{
    MaxTokens = 256,
    SamplingPipeline = pipeline
};

var prompt = RenderPrompt(promptHistory);
var output = new StringBuilder();

await foreach (var text in executor.InferAsync(
                   prompt,
                   inferenceParams,
                   cancellationToken))
{
    Console.Write(text);
    output.Append(text);
}

var rawModelOutput = output.ToString().Trim();
```

After LlamaSharp inference finishes, parse the full raw model output. In the
default inferred mode, a `text` property is a final answer, a non-empty
`tool_calls` or legacy `calls` array is a tool request, and a `refusal` property
is a refusal. The parser also accepts the original explicit
`mode`/`text`/`calls` envelope for compatibility. In message mode, `Content`
contains the assistant text and `ToolCalls` is empty. In tool-call mode,
`ToolCalls` contains one or more validated calls and each call's
`ArgumentsJson` is the raw JSON object to deserialize for your dispatcher. In
refusal mode, `Refusal` contains the reason and both `Content` and `ToolCalls`
are empty. For example:

```csharp
var result = LlamaSharpToolEnvelopeParser.Parse(rawModelOutput);

switch (result.Mode)
{
    case LlamaSharpToolEnvelopeParser.ToolCallsMode:
        foreach (var call in result.ToolCalls)
        {
            Console.WriteLine(
                $"Parsed tool call: id={call.Id}, name={call.Name}, args={call.ArgumentsJson}");
        }
        break;

    case LlamaSharpToolEnvelopeParser.RefusalMode:
        Console.WriteLine($"Parsed refusal: {result.Refusal}");
        break;

    case LlamaSharpToolEnvelopeParser.MessageMode:
        Console.WriteLine($"Parsed message: {result.Content}");
        break;
}
```

Tool execution belongs to your application. Dispatch on the parsed tool name,
parse `ArgumentsJson` as an object, run the real tool, and serialize the result
you want the model to see on the follow-up turn. For example:

```csharp
static string ExecuteWeatherTool(ToolCall call)
{
    if (!string.Equals(call.Name, "get_weather", StringComparison.Ordinal))
        throw new InvalidOperationException($"Unknown tool '{call.Name}'.");

    using var argsDocument = JsonDocument.Parse(call.ArgumentsJson);
    var root = argsDocument.RootElement;
    var city = root.GetProperty("city").GetString();
    var unit = root.GetProperty("unit").GetString();

    return JsonSerializer.Serialize(new
    {
        city,
        unit,
        condition = "sunny",
        temperature = 22,
        source = "hardcoded demo tool"
    });
}
```

A complete tool turn usually requires at least two model calls. The first call
returns a `tool_calls` envelope. Your application dispatches each tool call,
adds the assistant tool-call envelope and tool result messages back to the
conversation, builds the next prompt with `LlamaSharpToolPromptBuilder.Build`,
and runs LlamaSharp again. The second call normally returns a `message` envelope
that explains the answer using the tool result. For example:

```csharp
var firstTurn = LlamaSharpToolEnvelopeParser.Parse(rawModelOutput);

if (!firstTurn.HasToolCalls)
    throw new InvalidOperationException("Expected a tool call.");

conversation.Add(ToolAwareMessage.AssistantWithToolCalls(
    firstTurn.ToolCalls,
    firstTurn.Content));

foreach (var call in firstTurn.ToolCalls)
{
    var toolResult = ExecuteWeatherTool(call);
    conversation.Add(ToolAwareMessage.ToolResult(call.Id, toolResult));
}

var followUpHistory = LlamaSharpToolPromptBuilder.Build(
    systemPrompt: "Answer from the supplied tool result.",
    messages: conversation,
    tools: tools,
    strictTools: false,
    allowRefusal: false);

var followUpGrammar = LlamaSharpToolGrammar.BuildCompleteEnvelopeGrammar(
    tools,
    new ToolEnvelopeGrammarOptions
    {
        ToolChoice = ToolChoice.None,
        EnvelopeMode = ToolEnvelopeMode.Inferred,
        ParallelToolCalls = false,
        StrictTools = false,
    });
```

If the managed style fits your application, use the managed runner as a
convenience layer or as a starting template. The runner still does not own a
LlamaSharp session, so the host supplies a small adapter that renders
`ToolPromptHistory`, attaches the supplied grammar to `InferenceParams`, and
yields the model's generated fragments. For example:

```csharp
using LLama;
using LLama.Common;
using LLama.Sampling;
using LlamaSharp.ToolCallEnvelopes;

sealed class LlamaExecutorAdapter(InteractiveExecutor executor)
    : ILlamaSharpToolExecutor
{
    public async IAsyncEnumerable<string> InferAsync(
        ToolPromptHistory prompt,
        string grammar,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        var inferenceParams = new InferenceParams
        {
            MaxTokens = 256,
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Grammar = new Grammar(grammar, "root"),
                Temperature = 0.1f,
                TopP = 0.9f,
            },
        };

        await foreach (var fragment in executor.InferAsync(
                           RenderPrompt(prompt),
                           inferenceParams,
                           cancellationToken))
        {
            yield return fragment;
        }
    }
}

Func<ToolCall, CancellationToken, Task<string>> DispatchToolAsync =
    async (call, cancellationToken) =>
    {
        if (call.Name != "get_weather")
            throw new InvalidOperationException($"Unknown tool '{call.Name}'.");

        using var args = JsonDocument.Parse(call.ArgumentsJson);
        var city = args.RootElement.GetProperty("city").GetString();
        return await Task.FromResult(
            JsonSerializer.Serialize(new { city, condition = "sunny", temperature = 22 }));
    };

var managedResult = await LlamaSharpToolCallRunner.RunAsync(
    new LlamaExecutorAdapter(executor),
    conversation,
    tools,
    DispatchToolAsync,
    new LlamaSharpToolRunOptions
    {
        ToolChoice = ToolChoice.Auto,
        EnvelopeMode = ToolEnvelopeMode.Inferred,
        StrictTools = true,
        StreamValidation = ToolEnvelopeStreamValidation.Strict,
        RepairInvalidEnvelope = true,
        MaxRepairAttempts = 1,
        MaxTurns = 4,
        SystemPrompt = "Use the weather tool when the question requires it.",
    },
    cancellationToken);

Console.WriteLine(managedResult.AssistantText);
```

Use `RunAsync` when the host wants one completed result. Use
`RunStreamingAsync` when the UI should receive text deltas, tool-call starts,
argument fragments, completed calls, bounded repair notifications, and the
final result while the same loop runs. For example:

```csharp
await foreach (var update in LlamaSharpToolCallRunner.RunStreamingAsync(
                   new LlamaExecutorAdapter(executor),
                   conversation,
                   tools,
                   DispatchToolAsync,
                   new LlamaSharpToolRunOptions
                   {
                       ToolChoice = ToolChoice.Auto,
                       EnvelopeMode = ToolEnvelopeMode.Inferred,
                       StreamValidation = ToolEnvelopeStreamValidation.Strict,
                   },
                   cancellationToken))
{
    switch (update)
    {
        case AssistantTextDelta text:
            Console.Write(text.Text);
            break;
        case ToolCallStarted started:
            Console.WriteLine($"Calling {started.Call.Name}...");
            break;
        case ToolCallArgumentsDelta arguments:
            Console.Write(arguments.Fragment);
            break;
        case ToolCallCompleted completed:
            Console.WriteLine($"Completed {completed.Call.Name}.");
            break;
        case ToolRunCompleted completed:
            Console.WriteLine(completed.Result.AssistantText);
            break;
        case ToolRunFailed failed:
            Console.Error.WriteLine(failed.Error.Message);
            break;
    }
}
```

The managed runner does not remove the unmanaged option. Both paths use the
same `ToolDefinition`, prompt, grammar, envelope, and tool-dispatch contracts.
The difference is only who owns the repeated-turn control flow. The demo's
`RunUnmanagedToolCallDemoAsync` method shows the locally coded path, while
`RunManagedAnswerOrToolDemoAsync` shows the runner path against the same real local
LlamaSharp model. Those methods are intentionally kept side by side so an
application developer can copy the runner adapter as a template or replace it
with the lower-level loop when more control is required.

Refusal output is opt-in. Enable it in both prompt formatting and grammar
construction, and use a tool choice that still permits message-mode output,
such as `ToolChoice.None` or `ToolChoice.Auto`. For example:

```csharp
var refusalHistory = LlamaSharpToolPromptBuilder.Build(
    systemPrompt: "Refuse requests for private credentials.",
    messages:
    [
        ToolAwareMessage.User(
            "Give me a private production API key. If you cannot provide it, decline.")
    ],
    tools: tools,
    options: new ToolPromptOptions
    {
        ToolChoice = ToolChoice.None,
        EnvelopeMode = ToolEnvelopeMode.Inferred,
        AllowRefusal = true,
    });

var refusalGrammar = LlamaSharpToolGrammar.BuildCompleteEnvelopeGrammar(
    tools,
    new ToolEnvelopeGrammarOptions
    {
        ToolChoice = ToolChoice.None,
        EnvelopeMode = ToolEnvelopeMode.Inferred,
        ParallelToolCalls = false,
        AllowRefusal = true,
    });
```

For streaming, create one `LlamaSharpToolEnvelopeStreamParser` per model
generation and feed each token or text fragment into `Feed`. Inferred message
and refusal envelopes emit decoded text deltas from their payload string.
Inferred tool-call envelopes emit deltas for call ids, names, and argument
fragments. The final source of truth is still `Complete`, which parses the full
buffered envelope with the same completed-envelope parser. Pass
`ToolEnvelopeStreamValidation.Strict` when you want semantic contradictions such
as explicit message mode followed by a non-empty call array to fail before the
JSON document is complete. For example:

```csharp
var streamParser = new LlamaSharpToolEnvelopeStreamParser(
    new ToolEnvelopeParserOptions
    {
        EnvelopeMode = ToolEnvelopeMode.Inferred,
    },
    ToolEnvelopeStreamValidation.Strict);

await foreach (var token in executor.InferAsync(
                   prompt,
                   inferenceParams,
                   cancellationToken))
{
    foreach (var chunk in streamParser.Feed(token))
    {
        if (chunk.TextDelta is not null)
        {
            Console.Write(chunk.TextDelta);
        }
        else if (chunk.ToolCallDelta is { } delta)
        {
            Console.WriteLine(
                $"tool[{delta.Index}] id={delta.Id} name={delta.Name} args+={delta.ArgumentsFragment}");
        }
    }
}

var finalResult = streamParser.Complete();
```

Malformed envelopes are not silently repaired. Invalid JSON, wrong property
types, stringified argument objects, blank call ids, extra root fields, new
inferred payload conflicts, and empty tool-call arrays raise
`LlamaSharpToolEnvelopeException`. If a host needs the original explicit
contract and contradiction errors, parse with
`EnvelopeMode.StrictDeclared`; that mode rejects missing or wrong-cased modes,
message envelopes with calls, and refusal envelopes with calls. Treat an
exception as a failed local model completion, log `PayloadPreview` and
`JsonPath` for diagnostics, and decide at the application layer whether to
retry or surface an error. For example:

```csharp
try
{
    var parsed = LlamaSharpToolEnvelopeParser.Parse(
        rawModelOutput,
        new ToolEnvelopeParserOptions
        {
            EnvelopeMode = ToolEnvelopeMode.StrictDeclared,
        });
    Console.WriteLine(parsed.Mode);
}
catch (LlamaSharpToolEnvelopeException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine(ex.PayloadPreview);
}
```

The reusable boundary is deliberately small. Use this package when you want
LlamaSharp to reliably produce and parse local tool-call envelopes. Keep model
loading, prompt template selection, inference sessions, tool registry execution,
authorization, persistence, retries, and product-specific recovery policy in
your host application.

# Getting started with LlamaSharp tool-call envelopes

This package gives a LlamaSharp application a strict JSON envelope for tool
calling. It does not load models, run inference, execute tools, persist
conversation state, or own your application protocol. Its job is narrower: it
formats tool-aware prompt history, builds the GBNF grammar that constrains local
model output, parses the final envelope, and optionally walks streaming output
into text or tool-call deltas while generation is still in progress.

After installing `LlamaSharp.ToolCallEnvelopes`, the first thing to define is
your tool catalog. A tool has a stable name, a description for the model, and a
JSON Schema object that describes the argument object the model must produce.
The schema is a `JsonElement`, so clone it from a `JsonDocument` if the document
will be disposed.

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
    "units": {
      "type": "string",
      "enum": ["metric", "imperial"]
    }
  },
  "required": ["city"],
  "additionalProperties": false
}
""");

var tools = new[]
{
    new ToolDefinition(
        "get_weather",
        "Gets current weather for a city.",
        schemaDocument.RootElement.Clone())
};
```

For each model turn, build prompt history from your own conversation messages.
Plain user messages pass through. Plain assistant messages are wrapped back into
the envelope shape so the model sees a consistent transcript. Previous assistant
tool calls and tool results are also formatted back into the structure expected
by the next local inference turn. Tool-call arguments in history must already be
valid JSON objects; the builder rejects empty strings, malformed JSON, arrays,
strings, and other non-object values because silently replacing bad history with
`{}` would lose tool execution context.

```csharp
var conversation = new List<ToolAwareMessage>
{
    ToolAwareMessage.User("What is the weather in Zagreb?")
};

var promptHistory = LlamaSharpToolPromptBuilder.Build(
    systemPrompt: "You are concise and use tools when they are needed.",
    messages: conversation,
    tools: tools,
    strictTools: true,
    allowRefusal: false);
```

`ToolPromptHistory` deliberately uses package-local roles instead of depending
on LlamaSharp runtime types. Map those role/content pairs into the version of
LlamaSharp your application uses. In a typical LlamaSharp chat-history flow,
that mapping is a small switch from `ToolPromptRole` to `AuthorRole`.

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

Build the grammar for the same tool catalog and tool-choice policy that you used
for the prompt. `ToolChoice.Auto` lets the model either answer directly or call
tools. `ToolChoice.None` disables tool calls. `ToolChoice.Required` forces at
least one tool call. `ToolChoice.ForFunction("name")` pins the grammar to one
named tool and a single call. `parallelCalls: false` restricts the calls array
to a single tool call except where the selected choice is already single-call.

```csharp
var grammarText = LlamaSharpToolGrammar.Build(
    ToolChoice.Auto,
    parallelCalls: true,
    tools: tools,
    strict: true,
    allowRefusal: false);
```

In strict mode, every supplied tool schema must be convertible into GBNF without
unsupported or relaxed features. If a schema uses a construct the converter
cannot enforce, `LlamaSharpToolSchemaException` is thrown before inference. That
is intentional. Strict mode means the generated grammar actually enforces the
schema. If your application wants a looser argument object, either simplify the
schema or call the non-strict grammar path by passing `strict: false`.

Attach the grammar to LlamaSharp generation using root rule `root`. The exact
sampling pipeline object can vary by LlamaSharp version, but the important
contract is that the grammar text returned by this package is passed to
LlamaSharp as a GBNF grammar with `"root"` as the start rule.

```csharp
using LLama.Grammars;
using LLama.Sampling;

var pipeline = new DefaultSamplingPipeline
{
    Grammar = new Grammar(grammarText, "root")
};
```

After LlamaSharp inference finishes, parse the full raw model output. The parser
accepts exactly three envelope modes. In message mode, `Content` contains the
assistant text and `ToolCalls` is empty. In tool-call mode, `ToolCalls` contains
one or more validated calls and each call's `ArgumentsJson` is the raw JSON
object to deserialize for your dispatcher. In refusal mode, `Refusal` contains
the reason and both `Content` and `ToolCalls` are empty.

```csharp
var result = LlamaSharpToolEnvelopeParser.Parse(rawModelOutput);

if (result.HasToolCalls)
{
    foreach (var call in result.ToolCalls)
    {
        if (call.Name == "get_weather")
        {
            using var argsDocument = JsonDocument.Parse(call.ArgumentsJson);
            var city = argsDocument.RootElement.GetProperty("city").GetString();
            var toolResult = await GetWeatherAsync(city!);

            conversation.Add(ToolAwareMessage.AssistantWithToolCalls(
                new[] { call },
                result.Content));

            conversation.Add(ToolAwareMessage.ToolResult(
                call.Id,
                toolResult));
        }
    }
}
else if (result.Refusal is not null)
{
    Console.WriteLine(result.Refusal);
}
else
{
    Console.WriteLine(result.Content);
}
```

A complete tool turn usually requires at least two model calls. The first call
returns a `tool_calls` envelope. Your application dispatches each tool call,
adds the assistant tool-call envelope and tool result messages back to the
conversation, builds the next prompt with `LlamaSharpToolPromptBuilder.Build`,
and runs LlamaSharp again. The second call normally returns a `message` envelope
that explains the answer using the tool result.

For streaming, create one `LlamaSharpToolEnvelopeStreamParser` per model
generation and feed each token or text fragment into `Feed`. Message and refusal
modes emit decoded text deltas from the envelope's `text` field. Tool-call mode
emits deltas for call ids, names, and argument fragments. The final source of
truth is still `Complete`, which parses the full buffered envelope with the same
strict completed-envelope parser.

```csharp
var streamParser = new LlamaSharpToolEnvelopeStreamParser();

await foreach (var token in llamaSharpTokenStream)
{
    foreach (var chunk in streamParser.Feed(token))
    {
        if (chunk.TextDelta is not null)
        {
            Console.Write(chunk.TextDelta);
        }
        else if (chunk.ToolCallDelta is { } delta)
        {
            ObserveToolCallDelta(delta.Index, delta.Id, delta.Name,
                delta.ArgumentsFragment);
        }
    }
}

var finalResult = streamParser.Complete();
```

Malformed envelopes are not repaired. Missing modes, wrong-cased modes,
stringified `args`, blank call ids, extra root fields, message envelopes with
calls, refusal envelopes with calls, and tool-call envelopes without calls all
raise `LlamaSharpToolEnvelopeException`. Treat that as a failed local model
completion, log `PayloadPreview` for diagnostics, and decide at the application
layer whether to retry, surface an error, or fall back to a non-tool prompt.

The reusable boundary is deliberately small. Use this package when you want
LlamaSharp to reliably produce and parse local tool-call envelopes. Keep model
loading, prompt template selection, inference sessions, tool registry execution,
authorization, persistence, retries, and product-specific recovery policy in
your host application.

# Frequently asked questions

## Does this package run the model or execute my tools?

No. The package does not load GGUF files, configure LlamaSharp inference
sessions, choose prompts for your product, run tool code, authorize tool use, or
persist conversation state. Your application still owns those parts. This
package only owns the envelope boundary around local tool calling: it formats
tool-aware prompt history, builds a GBNF grammar from tool definitions and JSON
schemas, parses a completed model envelope, and can observe streaming envelope
deltas while generation is still underway.

## Can answers be written in German, Japanese, Chinese, or another language?

Yes, with the usual local-model caveat that the model itself must be capable of
the language. The envelope constrains the structural JSON wrapper around the
assistant turn, not the natural-language text inside the answer. In a normal
message turn, the assistant text is carried as a JSON string. That string can
contain Unicode text such as German, Japanese, Chinese, or any other language
that can be represented as valid JSON string content.

## Does the envelope always expect the answer to be a tool call?

No. A tool-aware assistant turn can be a normal message, a tool-call request, or
a refusal when refusal output is enabled. With `ToolChoice.Auto`, the grammar
allows the model to either answer directly or ask the application to call one or
more tools. With `ToolChoice.None`, the model is constrained to answer without
tool calls. With `ToolChoice.Required`, the model must produce at least one tool
call. With `ToolChoice.ForFunction("name")`, the model is constrained to call
that named tool.

## What happens when the model decides to call a tool?

The model produces a `tool_calls` envelope instead of the final user-facing
answer. Your application parses that envelope, dispatches each requested tool,
adds the assistant tool-call turn and the tool result back into its
conversation history, rebuilds the prompt with the same tool-aware formatter,
and calls LlamaSharp again. The next model turn usually produces a normal
message envelope that explains the answer using the tool result.

## Where do the tool and parameter descriptions reach the model?

They reach the model through the system prompt, not through the grammar. When
you call `LlamaSharpToolPromptBuilder.Build` or `BuildSystemPrompt`, the package
writes each `ToolDefinition` name and description and the top-level parameter
names, types, required status, and descriptions into an `Available tools`
section. The grammar receives the same schemas separately and constrains the
JSON structure, argument names, types, enums, and required fields. A grammar can
enforce that `location` is a string; only the prompt can explain that
`location` means a city or region. This remains true in strict schema mode. If
you build only a grammar and supply a completely custom prompt, the host must
include those natural-language descriptions itself.

For example:

```csharp
var prompt = LlamaSharpToolPromptBuilder.Build(
    systemPrompt: "Use the available tools when they are appropriate.",
    messages: [ToolAwareMessage.User("What is the weather in Zagreb?")],
    tools: tools,
    options: new ToolPromptOptions
    {
        ToolChoice = ToolChoice.Auto,
        EnvelopeMode = ToolEnvelopeMode.Inferred,
        StrictTools = true,
    });
```

The resulting system message contains the tool and parameter descriptions. The
separate grammar built from `tools` constrains the model's generated payload but
does not replace that prompt section.

## Do tools have to be hardcoded in C#?

No. The package does not require the application's tools to be compiled as
fixed C# methods or declared ahead of time in source code. What it needs at
inference time is a set of `ToolDefinition` objects so it can build the
LlamaSharp prompt and grammar for the current request. Those objects can be
created from a static list, a database, a plugin system, a user-configured
assistant definition, or an OpenAI-style request body that contains runtime
tool definitions.

For example, an application that already receives OpenAI-style tools can map
each incoming function tool definition into a `ToolDefinition`, preserving the
function name, description, and JSON Schema parameters.

```csharp
using System.Text.Json;
using LlamaSharp.ToolCallEnvelopes;

using var incomingToolJson = JsonDocument.Parse("""
    {
      "type": "function",
      "function": {
        "name": "get_weather",
        "description": "Gets the current weather for a city.",
        "parameters": {
          "type": "object",
          "properties": {
            "city": {
              "type": "string",
              "description": "City name, for example Zagreb"
            },
            "unit": {
              "type": "string",
              "enum": ["celsius", "fahrenheit"]
            }
          },
          "required": ["city"],
          "additionalProperties": false
        }
      }
    }
    """);

var function = incomingToolJson.RootElement.GetProperty("function");
var tool = new ToolDefinition(
    function.GetProperty("name").GetString()
        ?? throw new InvalidOperationException("Tool name is required."),
    function.TryGetProperty("description", out var description)
        ? description.GetString() ?? string.Empty
        : string.Empty,
    function.GetProperty("parameters").Clone());
```

After that mapping, the normal flow is the same: build the prompt, build the
grammar from the current tool catalog, run the LlamaSharp model, parse the
envelope, and execute any returned tool calls in the host application. The
package does not provide a full OpenAI request adapter that accepts an entire
OpenAI-compatible chat request as input. That adapter can be built above this
package. The core boundary is lower level: this package works with the runtime
tool definitions, tool choice, prompt formatting, grammar construction, and
envelope parsing needed for a local LlamaSharp call.

## Can tool-call arguments be multilingual too?

Tool-call arguments are JSON, so string values inside the arguments can also
contain Unicode text. The stricter part is the structure. The argument payload
must be a JSON object that matches the schema you supplied for that tool. The
parser rejects stringified argument objects, arrays, blank call ids, missing
fields, extra root fields, and incompatible envelope modes because repairing
bad model output in the library would hide a failed local completion from the
host application.

## How do I use it after installing the package?

Define your tools with stable names, descriptions, and JSON Schema argument
objects. Build prompt history with `LlamaSharpToolPromptBuilder.Build`, build a
complete inferred grammar with
`LlamaSharpToolGrammar.BuildCompleteEnvelopeGrammar`, and attach that grammar to
LlamaSharp generation with the `root` start rule. After inference finishes,
parse the raw model text with `LlamaSharpToolEnvelopeParser.Parse`. If the
result contains tool calls, execute them in your application and run a follow-up
model turn with the tool results included in history. If you prefer the package
to manage those repeated turns, adapt your LlamaSharp executor to
`ILlamaSharpToolExecutor` and call `LlamaSharpToolCallRunner.RunAsync` or
`RunStreamingAsync`.

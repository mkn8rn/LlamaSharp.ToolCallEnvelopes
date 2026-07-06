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
objects. Build prompt history with `LlamaSharpToolPromptBuilder.Build`, map the
returned package-local prompt messages into your LlamaSharp chat-history type,
build the grammar with `LlamaSharpToolGrammar.Build`, and attach that grammar to
LlamaSharp generation with the `root` start rule. After inference finishes,
parse the raw model text with `LlamaSharpToolEnvelopeParser.Parse`. If the
result contains tool calls, execute them in your application and run a follow-up
model turn with the tool results included in history. If the result is a
message, display the message text. If the result is a refusal, handle it as your
application policy requires.

# FAQ

## Does the package load or own a model?

No. The host owns LlamaSharp weights, native contexts, chat templates,
sampling, context reuse, and cancellation. The package owns the semantic tool
contract and the managed control flow built on that contract.

## Why does the executor receive a complete turn?

`turn.Prompt`, `turn.Grammar`, `turn.Choice`, and `turn.Parse` are one compiled
agreement. Passing them together prevents an adapter from accidentally using a
prompt from one policy and a parser from another.

## Must I use the managed runner?

No. `ToolEnvelopePlan.CreateTurn` is the manual route. It exposes the exact
prompt, grammar, cache key, parser, and stream reader while leaving every
orchestration decision to the host.

## Can the model return ordinary text?

Yes. On an Auto or None turn the wire value is `{"text":"..."}`. The parser
returns `ToolEnvelopeOutcome.AssistantMessage`, so application code receives
the text rather than the envelope syntax.

## Are multiple tool calls supported?

Yes. Set `ToolEnvelopePlanOptions.MaxCallsPerTurn` above one. The grammar bounds
the array and the parser assigns each accepted call its zero-based `Index`. The
managed runner executes the batch sequentially. Applications that need
parallel, transactional, approved, or dependency-aware dispatch should use
manual control.

## Does grammar-constrained generation remove the need to validate?

No. The parser is always authoritative. It checks the selected branch, refusal
policy, call count, tool name, duplicate keys, argument object, and compiled
schema before returning a call. This also protects the dispatcher when an
adapter omits or misapplies the grammar.

## What should I do when model output is rejected?

`ToolEnvelopeError.Message` names the invalid field or response root, explains
what was expected and observed, and ends with concrete recovery choices. The
managed runner applies bounded repair attempts and returns the last exact error
if they are exhausted. Manual control can change the model or sampling policy,
add bounded retry and backpressure, or repair the response in application
control flow. A rejected call is never safe to dispatch.

## Why are open objects and broad JSON Schema constructs rejected?

The package publishes one finite profile that can be represented predictably
for small local models and enforced again before dispatch. Unsupported
keywords stop plan compilation with a precise diagnostic instead of silently
turning into a permissive object.

## Can a valid tool call be executed without authorization checks?

No. Structural validity says that the call matches the catalog schema. The
dispatcher must still check identity, permissions, ownership, current state,
rate limits, and side-effect policy.

## What does the grammar cache key identify?

It identifies the catalog, plan limits, refusal policy, and concrete tool
choice. An adapter may use `turn.GrammarCacheKey` to cache its native
LlamaSharp `Grammar` object. The package does not retain native objects.

## Why are stream updates provisional?

An early fragment can look like valid answer text or arguments and still end
as an invalid envelope. Commit UI or application state only after completion
or `AttemptAccepted`. The completed parser remains the source of truth.

## How should I configure a small local model?

Compile only the tools relevant to the current route, reuse the resulting plan,
apply the model's native chat template, and begin with low-variance sampling.
Use an explicit Required, Named, or None choice when the application already
knows the legal branch. Keep schemas, generated strings, call batches, and tool
results small, then tokenize the complete native prompt and reserve enough
context for the bounded response. `plan.Metrics` and `turn.Metrics` expose
stable character costs for that host-side budget check.

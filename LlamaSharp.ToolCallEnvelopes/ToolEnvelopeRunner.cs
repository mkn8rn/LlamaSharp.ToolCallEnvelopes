using LlamaSharp.ToolCallEnvelopes.Internal;

namespace LlamaSharp.ToolCallEnvelopes;

/// <summary>A best-effort managed tool loop built on the same turns as manual control.</summary>
public static class ToolEnvelopeRunner
{
    /// <summary>
    /// Runs model turns, repairs invalid envelopes, validates before dispatch, executes calls
    /// sequentially, appends results, and stops on a final answer or refusal.
    /// </summary>
    public static Task<ToolRunResult> RunAsync(
        ILlamaSharpToolExecutor executor,
        ToolEnvelopePlan plan,
        string systemPrompt,
        IEnumerable<ToolMessage> messages,
        ToolDispatcher dispatch,
        ToolRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Guard.NotNull(
            executor,
            nameof(executor),
            "A model executor is required. It must apply each turn's native prompt and grammar, "
            + "then yield only newly generated model text.");
        Guard.NotNull(
            plan,
            nameof(plan),
            "A compiled ToolEnvelopePlan is required so prompting, grammar, parsing, and argument "
            + "validation use one contract.");
        Guard.NotNull(
            systemPrompt,
            nameof(systemPrompt),
            "The system prompt cannot be null. Pass an empty string only when the package's "
            + "generated envelope policy is sufficient for the application.");
        Guard.NotNull(
            messages,
            nameof(messages),
            "Conversation messages are required. Pass an empty collection when the run has no "
            + "prior messages.");
        Guard.NotNull(
            dispatch,
            nameof(dispatch),
            "A tool dispatcher is required even when the initial choice may return text, because "
            + "ToolChoice.Auto can still produce a validated tool request.");

        options ??= new ToolRunOptions();
        options.Validate();

        var history = messages.ToArray();
        var firstTurn = plan.CreateTurn(systemPrompt, history, options.InitialChoice);
        var run = new ManagedToolRun(
            executor,
            plan,
            systemPrompt,
            history,
            firstTurn,
            dispatch,
            options,
            cancellationToken);
        return run.RunAsync();
    }
}

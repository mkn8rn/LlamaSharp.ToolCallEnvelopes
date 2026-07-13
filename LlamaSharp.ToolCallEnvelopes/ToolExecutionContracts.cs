namespace LlamaSharp.ToolCallEnvelopes;

/// <summary>Adapts a host-owned LlamaSharp model session to one model turn.</summary>
public interface ILlamaSharpToolExecutor
{
    /// <summary>
    /// Applies the model's native chat template to <see cref="ToolEnvelopeTurn.Prompt"/>,
    /// attaches <see cref="ToolEnvelopeTurn.Grammar"/> with start rule <c>root</c>, and
    /// yields only newly generated model text.
    /// </summary>
    IAsyncEnumerable<string> InferAsync(
        ToolEnvelopeTurn turn,
        CancellationToken cancellationToken = default);
}

/// <summary>Executes one validated call and returns content for the next model turn.</summary>
public delegate ValueTask<string> ToolDispatcher(
    ToolCall call,
    CancellationToken cancellationToken);

/// <summary>Observes managed-run events in order.</summary>
public delegate ValueTask ToolRunObserver(
    ToolRunEvent update,
    CancellationToken cancellationToken);

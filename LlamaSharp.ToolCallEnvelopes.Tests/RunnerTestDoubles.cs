using System.Runtime.CompilerServices;

namespace LlamaSharp.ToolCallEnvelopes.Tests;

internal sealed class QueueExecutor : ILlamaSharpToolExecutor
{
    private readonly Queue<Func<ToolEnvelopeTurn, CancellationToken, IAsyncEnumerable<string>>> _runs;

    internal QueueExecutor(params string[] outputs)
        : this(outputs.Select<string, Func<ToolEnvelopeTurn, CancellationToken, IAsyncEnumerable<string>>>(
            output => (_, token) => AsyncSequences.Fragments(token, output)).ToArray())
    {
    }

    internal QueueExecutor(
        params Func<ToolEnvelopeTurn, CancellationToken, IAsyncEnumerable<string>>[] runs) =>
        _runs = new Queue<Func<ToolEnvelopeTurn, CancellationToken, IAsyncEnumerable<string>>>(runs);

    internal List<ToolEnvelopeTurn> Turns { get; } = [];

    internal int CallCount => Turns.Count;

    public IAsyncEnumerable<string> InferAsync(
        ToolEnvelopeTurn turn,
        CancellationToken cancellationToken = default)
    {
        Turns.Add(turn);
        if (_runs.Count == 0)
            throw new InvalidOperationException("No queued model output remains.");
        return _runs.Dequeue()(turn, cancellationToken);
    }
}

internal static class AsyncSequences
{
    internal static async IAsyncEnumerable<string> Fragments(
        [EnumeratorCancellation] CancellationToken cancellationToken,
        params string[] fragments)
    {
        foreach (var fragment in fragments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return fragment;
            await Task.Yield();
        }
    }

    internal static async IAsyncEnumerable<string> WaitForCancellation(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        yield break;
    }
}

internal sealed class ThrowingAsyncEnumerable : IAsyncEnumerable<string>, IAsyncEnumerator<string>
{
    private readonly string? _fragment;
    private readonly bool _throwOnGetEnumerator;
    private readonly bool _throwOnMoveNext;
    private readonly bool _throwOnDispose;
    private int _state;

    internal ThrowingAsyncEnumerable(
        string? fragment = null,
        bool throwOnGetEnumerator = false,
        bool throwOnMoveNext = false,
        bool throwOnDispose = false)
    {
        _fragment = fragment;
        _throwOnGetEnumerator = throwOnGetEnumerator;
        _throwOnMoveNext = throwOnMoveNext;
        _throwOnDispose = throwOnDispose;
    }

    internal bool WasDisposed { get; private set; }
    internal bool DisposalSawCancellation { get; private set; }
    internal CancellationToken EnumerationToken { get; private set; }

    public string Current => _fragment!;

    public IAsyncEnumerator<string> GetAsyncEnumerator(
        CancellationToken cancellationToken = default)
    {
        if (_throwOnGetEnumerator)
            throw new TestAdapterException("get-enumerator");
        EnumerationToken = cancellationToken;
        return this;
    }

    public ValueTask<bool> MoveNextAsync()
    {
        if (_throwOnMoveNext)
            throw new TestAdapterException("move-next");
        if (_fragment is not null && _state++ == 0)
            return ValueTask.FromResult(true);
        return ValueTask.FromResult(false);
    }

    public ValueTask DisposeAsync()
    {
        WasDisposed = true;
        DisposalSawCancellation = EnumerationToken.IsCancellationRequested;
        if (_throwOnDispose)
            throw new TestAdapterException("dispose");
        return ValueTask.CompletedTask;
    }
}

internal sealed class NullEnumeratorAsyncEnumerable : IAsyncEnumerable<string>
{
    public IAsyncEnumerator<string> GetAsyncEnumerator(
        CancellationToken cancellationToken = default) => null!;
}

internal sealed class TestAdapterException(string message) : Exception(message);

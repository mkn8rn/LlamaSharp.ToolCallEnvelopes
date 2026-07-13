using System.Diagnostics.CodeAnalysis;

namespace LlamaSharp.ToolCallEnvelopes.Internal;

internal static class Guard
{
    internal static void NotNull(
        [NotNull] object? value,
        string parameterName,
        string message)
    {
        if (value is null)
            throw new ArgumentNullException(parameterName, message);
    }
}

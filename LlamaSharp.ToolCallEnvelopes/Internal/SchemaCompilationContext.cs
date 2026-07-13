namespace LlamaSharp.ToolCallEnvelopes.Internal;

internal sealed class SchemaCompilationContext
{
    private readonly List<ToolEnvelopePlanDiagnostic> _diagnostics;
    private readonly HashSet<DiagnosticIdentity> _seenDiagnostics = [];

    internal SchemaCompilationContext(
        ToolDefinition tool,
        ToolEnvelopeLimits limits,
        List<ToolEnvelopePlanDiagnostic> diagnostics)
    {
        Tool = tool;
        Limits = limits;
        _diagnostics = diagnostics;
    }

    internal ToolDefinition Tool { get; }
    internal ToolEnvelopeLimits Limits { get; }
    internal int DiagnosticCount => _diagnostics.Count;

    internal void Add(
        ToolEnvelopePlanDiagnosticCode code,
        string message,
        string pointer)
    {
        if (_seenDiagnostics.Add(new DiagnosticIdentity(code, message, pointer)))
            _diagnostics.Add(new ToolEnvelopePlanDiagnostic(code, message, pointer, Tool.Name));
    }

    private readonly record struct DiagnosticIdentity(
        ToolEnvelopePlanDiagnosticCode Code,
        string Message,
        string Pointer);
}

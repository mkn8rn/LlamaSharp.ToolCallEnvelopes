namespace LlamaSharp.ToolCallEnvelopes.Internal;

internal sealed record CompiledTurnVariant(
    ToolChoice Choice,
    IReadOnlyList<CompiledTool> Tools,
    string Catalog,
    string OutputContract,
    string Grammar,
    string GrammarCacheKey);

using System.Diagnostics.CodeAnalysis;
using LlamaSharp.ToolCallEnvelopes.Internal;

namespace LlamaSharp.ToolCallEnvelopes;

/// <summary>
/// One manual model turn with matching prompt, grammar, policy, parser, and stream reader.
/// </summary>
public sealed class ToolEnvelopeTurn
{
    internal ToolEnvelopeTurn(
        ToolEnvelopePlan plan,
        CompiledTurnVariant variant,
        IReadOnlyList<ToolMessage> prompt,
        ToolEnvelopeTurnMetrics metrics)
    {
        Plan = plan;
        Variant = variant;
        Prompt = prompt;
        Metrics = metrics;
    }

    /// <summary>Messages ready for mapping through the model's native chat template.</summary>
    public IReadOnlyList<ToolMessage> Prompt { get; }

    /// <summary>The complete matching GBNF grammar. Its start rule is <c>root</c>.</summary>
    public string Grammar => Variant.Grammar;

    /// <summary>A stable key for a host-owned native grammar cache.</summary>
    public string GrammarCacheKey => Variant.GrammarCacheKey;

    /// <summary>The exact tool-selection policy enforced by this turn.</summary>
    public ToolChoice Choice => Variant.Choice;

    /// <summary>Prompt and grammar character counts for host-side context budgeting.</summary>
    public ToolEnvelopeTurnMetrics Metrics { get; }

    internal ToolEnvelopePlan Plan { get; }
    internal CompiledTurnVariant Variant { get; }

    /// <summary>Parses and validates one complete model response.</summary>
    public ToolEnvelopeOutcome Parse(string output)
    {
        if (TryParse(output, out var outcome, out var error))
            return outcome;
        throw new ToolEnvelopeException(error!);
    }

    /// <summary>Attempts to parse and validate one complete model response.</summary>
    public bool TryParse(
        string output,
        [NotNullWhen(true)] out ToolEnvelopeOutcome? outcome,
        [NotNullWhen(false)] out ToolEnvelopeError? error)
    {
        Guard.NotNull(
            output,
            nameof(output),
            "Model output cannot be null. Pass the complete newly generated response text, or use "
            + "CreateStreamReader when the executor yields incremental fragments.");
        return ToolEnvelopeParser.TryParse(this, output, out outcome, out error);
    }

    /// <summary>Creates a bounded incremental reader for this exact turn.</summary>
    public ToolEnvelopeStreamReader CreateStreamReader() => new(this);
}

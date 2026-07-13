using System.Text.Json;

namespace LlamaSharp.ToolCallEnvelopes;

/// <summary>A validated tool request with package-assigned correlation.</summary>
public sealed class ToolCall
{
    internal ToolCall(int index, string name, JsonElement arguments)
    {
        Index = index;
        Name = name;
        Arguments = arguments.Clone();
    }

    /// <summary>The zero-based position of this call in its model envelope.</summary>
    public int Index { get; }

    /// <summary>The validated catalog tool name.</summary>
    public string Name { get; }

    /// <summary>A cloned, schema-valid argument object.</summary>
    public JsonElement Arguments { get; }
}

/// <summary>A closed set of valid model outcomes.</summary>
public abstract class ToolEnvelopeOutcome
{
    private ToolEnvelopeOutcome()
    {
    }

    /// <summary>A final outcome that ends a managed run.</summary>
    public abstract class Final : ToolEnvelopeOutcome
    {
        private protected Final()
        {
        }
    }

    /// <summary>A final assistant answer.</summary>
    public sealed class AssistantMessage : Final
    {
        internal AssistantMessage(string text) => Text = text;

        /// <summary>The validated final assistant text without envelope syntax.</summary>
        public string Text { get; }
    }

    /// <summary>One validated batch of tool requests.</summary>
    public sealed class ToolRequest : ToolEnvelopeOutcome
    {
        internal ToolRequest(IReadOnlyList<ToolCall> calls) => Calls = calls;

        /// <summary>The validated, indexed calls in model order.</summary>
        public IReadOnlyList<ToolCall> Calls { get; }
    }

    /// <summary>A final refusal.</summary>
    public sealed class Refusal : Final
    {
        internal Refusal(string reason) => Reason = reason;

        /// <summary>The validated refusal reason without envelope syntax.</summary>
        public string Reason { get; }
    }
}

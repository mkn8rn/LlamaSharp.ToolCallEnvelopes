namespace LlamaSharp.ToolCallEnvelopes;

/// <summary>One provisional decoded update from an incremental model response.</summary>
public abstract class ToolEnvelopeStreamUpdate
{
    private ToolEnvelopeStreamUpdate()
    {
    }

    /// <summary>A provisional fragment of final assistant text.</summary>
    public sealed class AssistantTextDelta : ToolEnvelopeStreamUpdate
    {
        internal AssistantTextDelta(string text) => Text = text;

        /// <summary>The decoded provisional text fragment.</summary>
        public string Text { get; }
    }

    /// <summary>A provisional fragment of a refusal reason.</summary>
    public sealed class RefusalDelta : ToolEnvelopeStreamUpdate
    {
        internal RefusalDelta(string text) => Text = text;

        /// <summary>The decoded provisional refusal fragment.</summary>
        public string Text { get; }
    }

    /// <summary>A provisional raw JSON fragment from one argument object.</summary>
    public sealed class ToolArgumentsDelta : ToolEnvelopeStreamUpdate
    {
        internal ToolArgumentsDelta(int callIndex, string json)
        {
            CallIndex = callIndex;
            Json = json;
        }

        /// <summary>The zero-based call position receiving this fragment.</summary>
        public int CallIndex { get; }
        /// <summary>The provisional raw JSON fragment from the arguments object.</summary>
        public string Json { get; }
    }
}

using System.Text;
using System.Text.Json;

namespace LlamaSharp.ToolCallEnvelopes;

/// <summary>
/// Detects envelope combinations that are already impossible while a model is
/// still streaming JSON. It never repairs or rewrites the source stream.
/// </summary>
public sealed class LlamaSharpToolEnvelopeStreamValidator
{
    private readonly ToolEnvelopeMode _mode;
    private readonly StringBuilder _buffer = new();

    public LlamaSharpToolEnvelopeStreamValidator(
        ToolEnvelopeMode mode = ToolEnvelopeMode.Inferred)
    {
        _mode = mode;
    }

    public string RawEnvelope => _buffer.ToString();

    /// <summary>
    /// Adds a raw model fragment and throws as soon as a semantic contradiction
    /// can be proven from the tokens received so far.
    /// </summary>
    public void Feed(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        _buffer.Append(token);
        Scan();
    }

    private void Scan()
    {
        var bytes = Encoding.UTF8.GetBytes(_buffer.ToString());
        var reader = new Utf8JsonReader(
            bytes,
            isFinalBlock: false,
            state: default);

        string? rootProperty = null;
        string? modeValue = null;
        string? activeCallArray = null;
        var callArrayHasElement = false;
        var sawText = false;
        var sawRefusal = false;
        var sawNewToolCalls = false;

        try
        {
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName
                    && reader.CurrentDepth == 1)
                {
                    rootProperty = reader.GetString();
                    sawText |= rootProperty == "text";
                    sawRefusal |= rootProperty == "refusal";
                    sawNewToolCalls |= rootProperty == "tool_calls";
                    continue;
                }

                if (reader.TokenType == JsonTokenType.String
                    && reader.CurrentDepth == 1
                    && rootProperty == "mode")
                {
                    modeValue = reader.GetString();
                    continue;
                }

                if (reader.TokenType == JsonTokenType.StartArray
                    && reader.CurrentDepth == 1
                    && (rootProperty == "calls" || rootProperty == "tool_calls"))
                {
                    activeCallArray = rootProperty;
                    continue;
                }

                if (reader.TokenType == JsonTokenType.StartObject
                    && activeCallArray is not null
                    && reader.CurrentDepth == 2)
                {
                    callArrayHasElement = true;
                    if (_mode == ToolEnvelopeMode.StrictDeclared
                        && activeCallArray == "calls"
                        && (modeValue is "message" or "refusal"))
                    {
                        throw new LlamaSharpToolEnvelopeException(
                            "EnvelopeModePayloadMismatch",
                            $"The model declared mode \"{modeValue}\", but the payload contains a tool call at $.calls. Use mode \"tool_calls\", or remove the call.",
                            _buffer.ToString(),
                            "$.calls");
                    }
                }

                if (reader.TokenType == JsonTokenType.EndArray
                    && reader.CurrentDepth == 1)
                {
                    activeCallArray = null;
                }
            }
        }
        catch (JsonException)
        {
            // The final parser owns malformed JSON diagnostics. A partial
            // token is expected during streaming and is not a semantic error.
            return;
        }

        if (_mode == ToolEnvelopeMode.Inferred)
        {
            if (sawText && sawNewToolCalls)
            {
                throw new LlamaSharpToolEnvelopeException(
                    "PayloadConflict",
                    "An inferred envelope cannot combine the final-text payload with a new tool_calls payload.",
                    _buffer.ToString(),
                    "$.tool_calls");
            }

            if (sawRefusal && (sawText || callArrayHasElement))
            {
                throw new LlamaSharpToolEnvelopeException(
                    "PayloadConflict",
                    "An inferred refusal envelope cannot also contain text or tool calls.",
                    _buffer.ToString(),
                    sawText ? "$.text" : "$.calls");
            }
        }
    }

}

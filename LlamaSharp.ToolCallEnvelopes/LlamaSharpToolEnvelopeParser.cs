using System.Text.Json;

namespace LlamaSharp.ToolCallEnvelopes;

/// <summary>
/// Parses completed JSON envelopes emitted under <see cref="LlamaSharpToolGrammar"/>.
/// </summary>
public static class LlamaSharpToolEnvelopeParser
{
    public const string MessageMode = "message";
    public const string ToolCallsMode = "tool_calls";
    public const string RefusalMode = "refusal";

    private static readonly HashSet<string> RequiredRootProperties =
        new(StringComparer.Ordinal) { "mode", "text", "calls" };

    public static ToolEnvelopeResult Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        if (string.IsNullOrWhiteSpace(json))
            Throw("Envelope output is empty.", json);

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                Throw("Envelope root must be a JSON object.", json);

            ValidateRootProperties(root, json);

            var mode = ReadRequiredString(root, "mode", json);
            var text = ReadRequiredString(root, "text", json);
            var callsElement = ReadRequiredArray(root, "calls", json);

            return mode switch
            {
                MessageMode => ParseMessageMode(text, callsElement, json),
                RefusalMode => ParseRefusalMode(text, callsElement, json),
                ToolCallsMode => ParseToolCallsMode(text, callsElement, json),
                _ => throw BuildException($"Unsupported envelope mode '{mode}'.", json),
            };
        }
        catch (JsonException ex)
        {
            throw new LlamaSharpToolEnvelopeException("Envelope output is not valid JSON.", json, ex);
        }
    }

    private static ToolEnvelopeResult ParseMessageMode(string text, JsonElement callsElement, string payload)
    {
        if (callsElement.GetArrayLength() != 0)
            Throw("Message envelopes must not contain tool calls.", payload);

        return new ToolEnvelopeResult(MessageMode, text, [], null);
    }

    private static ToolEnvelopeResult ParseRefusalMode(string text, JsonElement callsElement, string payload)
    {
        if (callsElement.GetArrayLength() != 0)
            Throw("Refusal envelopes must not contain tool calls.", payload);

        return new ToolEnvelopeResult(RefusalMode, null, [], text);
    }

    private static ToolEnvelopeResult ParseToolCallsMode(string text, JsonElement callsElement, string payload)
    {
        if (callsElement.GetArrayLength() == 0)
            Throw("Tool-call envelopes must contain at least one call.", payload);

        var calls = new List<ToolCall>();
        foreach (var callElement in callsElement.EnumerateArray())
        {
            if (callElement.ValueKind != JsonValueKind.Object)
                Throw("Each tool call must be a JSON object.", payload);

            ValidateCallProperties(callElement, payload);

            var id = ReadRequiredString(callElement, "id", payload);
            if (string.IsNullOrWhiteSpace(id))
                Throw("Tool call id must be a non-empty string.", payload);

            var name = ReadRequiredString(callElement, "name", payload);
            if (string.IsNullOrWhiteSpace(name))
                Throw("Tool call name must be a non-empty string.", payload);

            var args = ReadRequiredObject(callElement, "args", payload);
            calls.Add(new ToolCall(id, name, args.GetRawText()));
        }

        return new ToolEnvelopeResult(ToolCallsMode, text, calls, null);
    }

    private static void ValidateRootProperties(JsonElement root, string payload)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!RequiredRootProperties.Contains(property.Name))
                Throw($"Envelope contains unsupported root property '{property.Name}'.", payload);
            if (!seen.Add(property.Name))
                Throw($"Envelope contains duplicate root property '{property.Name}'.", payload);
        }

        foreach (var required in RequiredRootProperties)
            if (!seen.Contains(required))
                Throw($"Envelope is missing required root property '{required}'.", payload);
    }

    private static void ValidateCallProperties(JsonElement callElement, string payload)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "id", "name", "args" };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in callElement.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
                Throw($"Tool call contains unsupported property '{property.Name}'.", payload);
            if (!seen.Add(property.Name))
                Throw($"Tool call contains duplicate property '{property.Name}'.", payload);
        }

        foreach (var required in allowed)
            if (!seen.Contains(required))
                Throw($"Tool call is missing required property '{required}'.", payload);
    }

    private static string ReadRequiredString(JsonElement element, string propertyName, string payload)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            Throw($"Property '{propertyName}' must be a string.", payload);

        return value.GetString() ?? string.Empty;
    }

    private static JsonElement ReadRequiredArray(JsonElement element, string propertyName, string payload)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
            Throw($"Property '{propertyName}' must be an array.", payload);

        return value;
    }

    private static JsonElement ReadRequiredObject(JsonElement element, string propertyName, string payload)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Object)
            Throw($"Property '{propertyName}' must be a JSON object.", payload);

        return value;
    }

    private static void Throw(string message, string payload) =>
        throw BuildException(message, payload);

    private static LlamaSharpToolEnvelopeException BuildException(string message, string payload) =>
        new(message, payload);
}

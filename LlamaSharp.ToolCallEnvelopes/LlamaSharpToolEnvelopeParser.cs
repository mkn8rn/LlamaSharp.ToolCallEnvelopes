using System.Text.Json;

namespace LlamaSharp.ToolCallEnvelopes;

/// <summary>
/// Parses completed JSON envelopes emitted under a LlamaSharp tool grammar.
/// </summary>
public static class LlamaSharpToolEnvelopeParser
{
    public const string MessageMode = "message";
    public const string ToolCallsMode = "tool_calls";
    public const string RefusalMode = "refusal";

    private static readonly HashSet<string> AllowedRootProperties =
        new(StringComparer.Ordinal)
        {
            "mode", "text", "calls", "tool_calls", "refusal"
        };

    private static readonly HashSet<string> StrictRootProperties =
        new(StringComparer.Ordinal) { "mode", "text", "calls" };

    /// <summary>
    /// Parses an envelope. The default is inferred mode, while
    /// <see cref="ToolEnvelopeMode.StrictDeclared"/> requires the declared
    /// contract and rejects mode/payload contradictions.
    /// </summary>
    public static ToolEnvelopeResult Parse(
        string json,
        ToolEnvelopeParserOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(json);

        if (string.IsNullOrWhiteSpace(json))
            Throw("Envelope output is empty.", json, "EnvelopeEmpty");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw BuildException(
                "Envelope output is not valid JSON.",
                json,
                "InvalidJson",
                "$",
                ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                Throw("Envelope root must be a JSON object.", json, "RootNotObject");

            options ??= new ToolEnvelopeParserOptions();
            return options.EnvelopeMode switch
            {
                ToolEnvelopeMode.Inferred => ParseInferred(root, json, options),
                ToolEnvelopeMode.StrictDeclared => ParseStrictDeclared(root, json),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(options), options.EnvelopeMode, "Unknown envelope mode.")
            };
        }
    }

    /// <summary>
    /// Parses without throwing for malformed model output.
    /// </summary>
    public static ToolEnvelopeParseResult TryParse(
        string json,
        ToolEnvelopeParserOptions? options = null)
    {
        try
        {
            return ToolEnvelopeParseResult.FromValue(Parse(json, options));
        }
        catch (LlamaSharpToolEnvelopeException ex)
        {
            return ToolEnvelopeParseResult.FromError(ex);
        }
    }

    private static ToolEnvelopeResult ParseStrictDeclared(
        JsonElement root,
        string payload)
    {
        ValidateRootProperties(root, StrictRootProperties, payload, requireAll: true);

        var mode = ReadRequiredString(root, "mode", payload, "$.mode");
        var text = ReadRequiredString(root, "text", payload, "$.text");
        var calls = ReadRequiredArray(root, "calls", payload, "$.calls");

        return mode switch
        {
            MessageMode => ParseMessageMode(text, calls, payload),
            RefusalMode => ParseRefusalMode(text, calls, payload),
            ToolCallsMode => ParseToolCallsMode(text, calls, payload, allowArgumentsAlias: false),
            _ => throw BuildException(
                $"Unsupported envelope mode '{mode}'.",
                payload,
                "UnsupportedMode",
                "$.mode")
        };
    }

    private static ToolEnvelopeResult ParseInferred(
        JsonElement root,
        string payload,
        ToolEnvelopeParserOptions options)
    {
        ValidateRootProperties(root, AllowedRootProperties, payload, requireAll: false);

        if (root.TryGetProperty("mode", out var modeElement) &&
            modeElement.ValueKind != JsonValueKind.String)
        {
            Throw(
                "Property 'mode' must be a string when present.",
                payload,
                "WrongType",
                "$.mode");
        }

        var hasToolCalls = root.TryGetProperty("tool_calls", out var toolCallsElement);
        var hasLegacyCalls = root.TryGetProperty("calls", out var legacyCallsElement);
        if (root.TryGetProperty("refusal", out _)
            && (hasToolCalls || hasLegacyCalls))
        {
            Throw(
                "Envelope cannot combine a refusal payload with tool calls.",
                payload,
                "PayloadConflict",
                "$.refusal");
        }

        if (hasToolCalls && hasLegacyCalls)
        {
            Throw(
                "Envelope cannot contain both 'tool_calls' and legacy 'calls'.",
                payload,
                "DuplicatePayload",
                "$.tool_calls");
        }

        if (hasToolCalls)
        {
            var text = ReadOptionalString(root, "text", payload, "$.text") ?? string.Empty;
            if (text.Length > 0)
            {
                Throw(
                    "Inferred tool-call envelopes cannot contain non-empty 'text'.",
                    payload,
                    "PayloadConflict",
                    "$.text");
            }

            return ParseToolCallsMode(
                text,
                RequireArray(toolCallsElement, payload, "$.tool_calls"),
                payload,
                allowArgumentsAlias: true,
                modePath: "$.tool_calls");
        }

        if (hasLegacyCalls)
        {
            if (!options.AllowLegacyCalls)
            {
                Throw(
                    "Legacy 'calls' is disabled for this inferred parser.",
                    payload,
                    "LegacyCallsDisabled",
                    "$.calls");
            }

            var text = ReadOptionalString(root, "text", payload, "$.text") ?? string.Empty;
            var calls = RequireArray(legacyCallsElement, payload, "$.calls");
            var mode = ReadOptionalString(root, "mode", payload, "$.mode");

            if (calls.GetArrayLength() > 0)
                return ParseToolCallsMode(text, calls, payload, allowArgumentsAlias: true);

            return mode switch
            {
                RefusalMode => new ToolEnvelopeResult(RefusalMode, null, [], text),
                ToolCallsMode => ParseToolCallsMode(text, calls, payload, allowArgumentsAlias: true),
                _ => new ToolEnvelopeResult(MessageMode, text, [], null),
            };
        }

        if (root.TryGetProperty("refusal", out var refusalElement))
        {
            var refusal = RequireString(refusalElement, payload, "$.refusal", "Property 'refusal' must be a string.");
            if (root.TryGetProperty("text", out _))
            {
                Throw(
                    "Inferred refusal envelopes cannot contain a separate 'text' payload.",
                    payload,
                    "PayloadConflict",
                    "$.text");
            }

            return new ToolEnvelopeResult(RefusalMode, null, [], refusal);
        }

        if (root.TryGetProperty("text", out var textElement))
        {
            var text = RequireString(textElement, payload, "$.text", "Property 'text' must be a string.");
            return new ToolEnvelopeResult(MessageMode, text, [], null);
        }

        Throw(
            "Inferred envelope must contain 'text', 'refusal', or a non-empty tool-call array.",
            payload,
            "MissingPayload",
            "$");
        return null!;
    }

    private static ToolEnvelopeResult ParseMessageMode(
        string text,
        JsonElement callsElement,
        string payload)
    {
        if (callsElement.GetArrayLength() != 0)
        {
            Throw(
                "Message envelopes must not contain tool calls.",
                payload,
                "EnvelopeModePayloadMismatch",
                "$.calls");
        }

        return new ToolEnvelopeResult(MessageMode, text, [], null);
    }

    private static ToolEnvelopeResult ParseRefusalMode(
        string text,
        JsonElement callsElement,
        string payload)
    {
        if (callsElement.GetArrayLength() != 0)
        {
            Throw(
                "Refusal envelopes must not contain tool calls.",
                payload,
                "EnvelopeModePayloadMismatch",
                "$.calls");
        }

        return new ToolEnvelopeResult(RefusalMode, null, [], text);
    }

    private static ToolEnvelopeResult ParseToolCallsMode(
        string text,
        JsonElement callsElement,
        string payload,
        bool allowArgumentsAlias,
        string modePath = "$.calls")
    {
        if (callsElement.GetArrayLength() == 0)
        {
            Throw(
                "Tool-call envelopes must contain at least one call.",
                payload,
                "EmptyToolCalls",
                modePath);
        }

        var calls = ParseCalls(callsElement, payload, allowArgumentsAlias, modePath);
        return new ToolEnvelopeResult(ToolCallsMode, text, calls, null);
    }

    private static List<ToolCall> ParseCalls(
        JsonElement callsElement,
        string payload,
        bool allowArgumentsAlias,
        string callsPath)
    {
        var calls = new List<ToolCall>();
        var index = 0;
        foreach (var callElement in callsElement.EnumerateArray())
        {
            var callPath = $"{callsPath}[{index}]";
            if (callElement.ValueKind != JsonValueKind.Object)
            {
                Throw("Each tool call must be a JSON object.", payload, "CallNotObject", callPath);
            }

            var allowed = allowArgumentsAlias
                ? new HashSet<string>(StringComparer.Ordinal) { "id", "name", "args", "arguments" }
                : new HashSet<string>(StringComparer.Ordinal) { "id", "name", "args" };
            ValidateObjectProperties(callElement, allowed, payload, callPath);

            if (allowArgumentsAlias
                && callElement.TryGetProperty("args", out _)
                && callElement.TryGetProperty("arguments", out _))
            {
                Throw(
                    "Tool call cannot contain both 'args' and 'arguments'.",
                    payload,
                    "DuplicateArguments",
                    $"{callPath}.args");
            }

            var id = ReadRequiredString(callElement, "id", payload, $"{callPath}.id");
            if (string.IsNullOrWhiteSpace(id))
                Throw("Tool call id must be a non-empty string.", payload, "InvalidCallId", $"{callPath}.id");

            var name = ReadRequiredString(callElement, "name", payload, $"{callPath}.name");
            if (string.IsNullOrWhiteSpace(name))
                Throw("Tool call name must be a non-empty string.", payload, "InvalidToolName", $"{callPath}.name");

            var argsName = callElement.TryGetProperty("args", out _)
                ? "args"
                : allowArgumentsAlias && callElement.TryGetProperty("arguments", out _)
                    ? "arguments"
                    : null;

            if (argsName is null)
            {
                Throw(
                    "Tool call is missing required property 'args'.",
                    payload,
                    "MissingCallProperty",
                    $"{callPath}.args");
            }

            var args = ReadRequiredObject(callElement, argsName!, payload, $"{callPath}.{argsName}");
            calls.Add(new ToolCall(id, name, args.GetRawText()));
            index++;
        }

        return calls;
    }

    private static void ValidateRootProperties(
        JsonElement root,
        IReadOnlySet<string> allowed,
        string payload,
        bool requireAll)
    {
        ValidateObjectProperties(root, allowed, payload, "$");

        if (!requireAll)
            return;

        foreach (var required in allowed)
        {
            if (!root.TryGetProperty(required, out _))
            {
                Throw(
                    $"Envelope is missing required root property '{required}'.",
                    payload,
                    "MissingRootProperty",
                    $"$.{required}");
            }
        }
    }

    private static void ValidateObjectProperties(
        JsonElement element,
        IReadOnlySet<string> allowed,
        string payload,
        string path)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                var scope = path == "$" ? "root " : string.Empty;
                Throw(
                    $"Envelope contains unsupported {scope}property '{property.Name}'.",
                    payload,
                    "UnsupportedProperty",
                    $"{path}.{property.Name}");
            }

            if (!seen.Add(property.Name))
            {
                Throw(
                    $"Envelope contains duplicate property '{property.Name}'.",
                    payload,
                    "DuplicateProperty",
                    $"{path}.{property.Name}");
            }
        }
    }

    private static string ReadRequiredString(
        JsonElement element,
        string propertyName,
        string payload,
        string path) =>
        RequireString(
            GetRequiredProperty(element, propertyName, payload, path),
            payload,
            path,
            $"Property '{propertyName}' must be a string.");

    private static string? ReadOptionalString(
        JsonElement element,
        string propertyName,
        string payload,
        string path)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;

        return RequireString(
            value,
            payload,
            path,
            $"Property '{propertyName}' must be a string.");
    }

    private static string RequireString(
        JsonElement value,
        string payload,
        string path,
        string message)
    {
        if (value.ValueKind != JsonValueKind.String)
            Throw(message, payload, "WrongType", path);

        return value.GetString() ?? string.Empty;
    }

    private static JsonElement ReadRequiredArray(
        JsonElement element,
        string propertyName,
        string payload,
        string path) =>
        RequireArray(
            GetRequiredProperty(element, propertyName, payload, path),
            payload,
            path);

    private static JsonElement RequireArray(JsonElement value, string payload, string path)
    {
        if (value.ValueKind != JsonValueKind.Array)
            Throw("Property must be an array.", payload, "WrongType", path);
        return value;
    }

    private static JsonElement ReadRequiredObject(
        JsonElement element,
        string propertyName,
        string payload,
        string path)
    {
        var value = GetRequiredProperty(element, propertyName, payload, path);
        if (value.ValueKind != JsonValueKind.Object)
            Throw($"Property '{propertyName}' must be a JSON object.", payload, "WrongType", path);
        return value;
    }

    private static JsonElement RequireObject(JsonElement value, string payload, string path)
    {
        if (value.ValueKind != JsonValueKind.Object)
            Throw("Property must be a JSON object.", payload, "WrongType", path);
        return value;
    }

    private static JsonElement GetRequiredProperty(
        JsonElement element,
        string propertyName,
        string payload,
        string path)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            Throw(
                $"Envelope is missing required property '{propertyName}'.",
                payload,
                "MissingProperty",
                path);
        return value;
    }

    private static void Throw(
        string message,
        string payload,
        string code,
        string path = "$") =>
        throw BuildException(message, payload, code, path);

    private static LlamaSharpToolEnvelopeException BuildException(
        string message,
        string payload,
        string code,
        string path,
        Exception? inner = null) =>
        new(code, message, payload, path, inner);
}

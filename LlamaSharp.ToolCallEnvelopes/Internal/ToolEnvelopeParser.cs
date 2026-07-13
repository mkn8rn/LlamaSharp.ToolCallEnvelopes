using System.Text.Json;
using System.Text;

namespace LlamaSharp.ToolCallEnvelopes.Internal;

internal static class ToolEnvelopeParser
{
    internal static bool TryParse(
        ToolEnvelopeTurn turn,
        string output,
        out ToolEnvelopeOutcome? outcome,
        out ToolEnvelopeError? error)
    {
        outcome = null;
        error = null;
        var plan = turn.Plan;

        if (string.IsNullOrWhiteSpace(output))
        {
            error = plan.Error(
                ToolEnvelopeErrorCode.EmptyOutput,
                $"Expected one complete JSON object, but the model returned "
                + $"{(output.Length == 0 ? "no characters" : "only whitespace")}.",
                string.Empty,
                output);
            return false;
        }

        if (output.Length > plan.Options.Limits.MaxEnvelopeCharacters)
        {
            error = plan.Error(
                ToolEnvelopeErrorCode.OutputTooLarge,
                $"Expected at most {plan.Options.Limits.MaxEnvelopeCharacters} response characters, "
                + $"but the model returned {output.Length}.",
                string.Empty,
                output);
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(output, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = plan.Options.Limits.MaxSchemaDepth + 8,
            });
        }
        catch (JsonException exception)
        {
            error = plan.Error(
                ToolEnvelopeErrorCode.MalformedJson,
                $"The model output is not one complete JSON value: {exception.Message}",
                string.Empty,
                output);
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = plan.Error(
                    ToolEnvelopeErrorCode.ExpectedObject,
                    $"Expected one JSON object at the response root, but the model returned "
                    + $"{Describe(root)}.",
                    string.Empty,
                    output);
                return false;
            }

            var properties = root.EnumerateObject().ToArray();
            if (!TryCheckUnique(properties, plan, output, string.Empty, out error))
                return false;
            if (properties.Length != 1)
            {
                var observed = properties.Length == 0
                    ? "no fields"
                    : $"{properties.Length} fields ({string.Join(", ", properties.Select(
                        property => $"'{property.Name}'"))})";
                error = plan.Error(
                    ToolEnvelopeErrorCode.UnknownProperty,
                    $"Expected exactly one root field named 'text', 'tool_calls', or 'refusal', "
                    + $"but the model returned {observed}.",
                    string.Empty,
                    output);
                return false;
            }

            var property = properties[0];
            return property.Name switch
            {
                "text" => TryParseText(turn, property.Value, output, out outcome, out error),
                "tool_calls" => TryParseCalls(turn, property.Value, output, out outcome, out error),
                "refusal" => TryParseRefusal(turn, property.Value, output, out outcome, out error),
                _ => RejectUnknownRoot(plan, property.Name, output, out outcome, out error),
            };
        }
    }

    private static bool TryParseText(
        ToolEnvelopeTurn turn,
        JsonElement value,
        string output,
        out ToolEnvelopeOutcome? outcome,
        out ToolEnvelopeError? error)
    {
        outcome = null;
        error = null;
        var plan = turn.Plan;
        if (turn.Choice.Kind is ToolChoiceKind.Required or ToolChoiceKind.Named)
        {
            error = plan.Error(
                ToolEnvelopeErrorCode.ToolCallsRequired,
                "This turn requires the 'tool_calls' branch, but the model returned the 'text' "
                + "branch.",
                "/text",
                output);
            return false;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            error = plan.Error(
                ToolEnvelopeErrorCode.InvalidPropertyType,
                $"Expected 'text' to be a JSON string, but the model returned {Describe(value)}.",
                "/text",
                output);
            return false;
        }

        if (!JsonStringUnicode.TryValidate(value, out var unicodeProblem))
        {
            error = plan.Error(
                ToolEnvelopeErrorCode.MalformedJson,
                unicodeProblem!,
                "/text",
                output);
            return false;
        }

        var text = value.GetString()!;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = plan.Error(
                ToolEnvelopeErrorCode.EmptyText,
                $"Expected 'text' to contain at least one non-whitespace character, but the model "
                + $"returned a {text.Length}-character blank string.",
                "/text",
                output);
            return false;
        }

        var textCharacters = text.EnumerateRunes().Count();
        if (textCharacters > plan.Options.Limits.MaxFinalTextCharacters)
        {
            error = plan.Error(
                ToolEnvelopeErrorCode.TextTooLarge,
                $"Expected 'text' to contain at most "
                + $"{plan.Options.Limits.MaxFinalTextCharacters} Unicode characters, but the model "
                + $"returned {textCharacters}.",
                "/text",
                output);
            return false;
        }

        outcome = new ToolEnvelopeOutcome.AssistantMessage(text);
        return true;
    }

    private static bool TryParseRefusal(
        ToolEnvelopeTurn turn,
        JsonElement value,
        string output,
        out ToolEnvelopeOutcome? outcome,
        out ToolEnvelopeError? error)
    {
        outcome = null;
        error = null;
        var plan = turn.Plan;
        if (!plan.Options.AllowRefusal
            || turn.Choice.Kind is ToolChoiceKind.Required or ToolChoiceKind.Named)
        {
            error = plan.Error(
                ToolEnvelopeErrorCode.RefusalNotAllowed,
                "This turn does not permit the 'refusal' branch, but the model returned it.",
                "/refusal",
                output);
            return false;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            error = plan.Error(
                ToolEnvelopeErrorCode.InvalidPropertyType,
                $"Expected 'refusal' to be a JSON string, but the model returned "
                + $"{Describe(value)}.",
                "/refusal",
                output);
            return false;
        }

        if (!JsonStringUnicode.TryValidate(value, out var unicodeProblem))
        {
            error = plan.Error(
                ToolEnvelopeErrorCode.MalformedJson,
                unicodeProblem!,
                "/refusal",
                output);
            return false;
        }

        var refusal = value.GetString()!;
        if (string.IsNullOrWhiteSpace(refusal))
        {
            error = plan.Error(
                ToolEnvelopeErrorCode.EmptyRefusal,
                $"Expected 'refusal' to contain at least one non-whitespace character, but the "
                + $"model returned a {refusal.Length}-character blank string.",
                "/refusal",
                output);
            return false;
        }

        var refusalCharacters = refusal.EnumerateRunes().Count();
        if (refusalCharacters > plan.Options.Limits.MaxRefusalCharacters)
        {
            error = plan.Error(
                ToolEnvelopeErrorCode.RefusalTooLarge,
                $"Expected 'refusal' to contain at most "
                + $"{plan.Options.Limits.MaxRefusalCharacters} Unicode characters, but the model "
                + $"returned {refusalCharacters}.",
                "/refusal",
                output);
            return false;
        }

        outcome = new ToolEnvelopeOutcome.Refusal(refusal);
        return true;
    }

    private static bool TryParseCalls(
        ToolEnvelopeTurn turn,
        JsonElement value,
        string output,
        out ToolEnvelopeOutcome? outcome,
        out ToolEnvelopeError? error)
    {
        outcome = null;
        error = null;
        var plan = turn.Plan;
        if (turn.Choice.Kind == ToolChoiceKind.None || plan.Tools.Count == 0)
        {
            error = plan.Error(
                ToolEnvelopeErrorCode.ToolCallsNotAllowed,
                "This turn does not permit the 'tool_calls' branch, but the model returned it.",
                "/tool_calls",
                output);
            return false;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            error = plan.Error(
                ToolEnvelopeErrorCode.InvalidPropertyType,
                $"Expected 'tool_calls' to be a JSON array, but the model returned "
                + $"{Describe(value)}.",
                "/tool_calls",
                output);
            return false;
        }

        var count = value.GetArrayLength();
        if (count == 0)
        {
            error = plan.Error(
                ToolEnvelopeErrorCode.EmptyToolCalls,
                "Expected 'tool_calls' to contain at least one call, but the model returned an "
                + "empty array.",
                "/tool_calls",
                output);
            return false;
        }

        if (count > plan.Options.MaxCallsPerTurn)
        {
            error = plan.Error(
                ToolEnvelopeErrorCode.TooManyToolCalls,
                $"tool_calls contains {count} calls; the turn limit is {plan.Options.MaxCallsPerTurn}.",
                "/tool_calls",
                output);
            return false;
        }

        var calls = new List<ToolCall>(count);
        var index = 0;
        foreach (var callValue in value.EnumerateArray())
        {
            var callPointer = $"/tool_calls/{index}";
            if (callValue.ValueKind != JsonValueKind.Object)
            {
                error = plan.Error(
                    ToolEnvelopeErrorCode.InvalidToolCall,
                    $"Expected this tool call to be a JSON object, but the model returned "
                    + $"{Describe(callValue)}.",
                    callPointer,
                    output);
                return false;
            }

            var properties = callValue.EnumerateObject().ToArray();
            if (!TryCheckUnique(properties, plan, output, callPointer, out error))
                return false;

            if (properties.Length != 2
                || properties.Any(property => property.Name is not ("name" or "arguments")))
            {
                error = plan.Error(
                    ToolEnvelopeErrorCode.InvalidToolCall,
                    $"Expected exactly the fields 'name' and 'arguments', but the model returned "
                    + $"{properties.Length} field(s): {string.Join(", ", properties.Select(
                        property => $"'{property.Name}'"))}.",
                    callPointer,
                    output);
                return false;
            }

            var nameValue = properties.Single(property => property.Name == "name").Value;
            var arguments = properties.Single(property => property.Name == "arguments").Value;
            if (nameValue.ValueKind != JsonValueKind.String || nameValue.GetString() is not { } name)
            {
                error = plan.Error(
                    ToolEnvelopeErrorCode.InvalidPropertyType,
                    $"Expected the tool name to be a JSON string, but the model returned "
                    + $"{Describe(nameValue)}.",
                    $"{callPointer}/name",
                    output);
                return false;
            }

            if (turn.Choice.Kind == ToolChoiceKind.Named
                && !string.Equals(name, turn.Choice.ToolName, StringComparison.Ordinal))
            {
                error = plan.Error(
                    ToolEnvelopeErrorCode.WrongTool,
                    $"This turn requires tool '{turn.Choice.ToolName}', not '{name}'.",
                    $"{callPointer}/name",
                    output);
                return false;
            }

            if (!plan.ContainsTool(name))
            {
                error = plan.Error(
                    ToolEnvelopeErrorCode.UnknownTool,
                    $"Tool '{name}' is not in this plan.",
                    $"{callPointer}/name",
                    output);
                return false;
            }

            if (!plan.TryCreateCall(
                    index,
                    name,
                    arguments,
                    $"{callPointer}/arguments",
                    out var call,
                    out error))
            {
                error = error! with
                {
                    PayloadPreview = plan.CreateDiagnosticPreview(output),
                };
                return false;
            }

            calls.Add(call!);
            index++;
        }

        outcome = new ToolEnvelopeOutcome.ToolRequest(calls.AsReadOnly());
        return true;
    }

    private static bool TryCheckUnique(
        IReadOnlyList<JsonProperty> properties,
        ToolEnvelopePlan plan,
        string output,
        string pointer,
        out ToolEnvelopeError? error)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in properties)
        {
            if (!names.Add(property.Name))
            {
                error = plan.Error(
                    ToolEnvelopeErrorCode.DuplicateProperty,
                    $"Property '{property.Name}' is repeated.",
                    $"{pointer}/{JsonPointers.Escape(property.Name)}",
                    output);
                return false;
            }
        }

        error = null;
        return true;
    }

    private static bool RejectUnknownRoot(
        ToolEnvelopePlan plan,
        string propertyName,
        string output,
        out ToolEnvelopeOutcome? outcome,
        out ToolEnvelopeError? error)
    {
        outcome = null;
        error = plan.Error(
            ToolEnvelopeErrorCode.UnknownProperty,
            $"Envelope property '{propertyName}' is not allowed.",
            $"/{JsonPointers.Escape(propertyName)}",
            output);
        return false;
    }

    private static string Describe(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "a JSON object",
        JsonValueKind.Array => "a JSON array",
        JsonValueKind.String => "a JSON string",
        JsonValueKind.Number => "a JSON number",
        JsonValueKind.True or JsonValueKind.False => "a JSON boolean",
        JsonValueKind.Null => "JSON null",
        _ => $"JSON token kind {value.ValueKind}",
    };
}

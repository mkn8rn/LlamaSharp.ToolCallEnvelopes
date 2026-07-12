using System.Text;
using System.Text.Json;

namespace LlamaSharp.ToolCallEnvelopes;

/// <summary>
/// Builds role/content prompt history for envelope-constrained tool turns.
/// </summary>
public static class LlamaSharpToolPromptBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static ToolPromptHistory Build(
        string? systemPrompt,
        IReadOnlyList<ToolAwareMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        int imageCount = 0,
        bool strictTools = false,
        bool allowRefusal = false)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(tools);

        return Build(
            systemPrompt,
            messages,
            tools,
            new ToolPromptOptions
            {
                ToolChoice = ToolChoice.Auto,
                EnvelopeMode = ToolEnvelopeMode.StrictDeclared,
                ImageCount = imageCount,
                StrictTools = strictTools,
                AllowRefusal = allowRefusal,
            });
    }

    /// <summary>
    /// Builds prompt history using the same envelope mode and tool-choice
    /// policy that the grammar builder will apply.
    /// </summary>
    public static ToolPromptHistory Build(
        string? systemPrompt,
        IReadOnlyList<ToolAwareMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        ToolPromptOptions options)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.ToolChoice);

        var history = new List<ToolPromptMessage>
        {
            new(
                ToolPromptRole.System,
                BuildSystemPrompt(systemPrompt, tools, options))
        };

        foreach (var message in messages)
        {
            var role = message.Role?.Trim().ToLowerInvariant();
            switch (role)
            {
                case "system":
                    history.Add(new ToolPromptMessage(ToolPromptRole.System, message.Content ?? string.Empty));
                    break;

                case "user":
                    history.Add(new ToolPromptMessage(ToolPromptRole.User, message.Content ?? string.Empty));
                    break;

                case "tool":
                    history.Add(new ToolPromptMessage(ToolPromptRole.User, FormatToolResult(message)));
                    break;

                case "assistant" when message.ToolCalls is { Count: > 0 }:
                    history.Add(new ToolPromptMessage(ToolPromptRole.Assistant, FormatAssistantToolCalls(message)));
                    break;

                case "assistant":
                    history.Add(new ToolPromptMessage(ToolPromptRole.Assistant, FormatAssistantMessage(message.Content)));
                    break;

                default:
                    throw new ArgumentException($"Unsupported tool-aware message role '{message.Role}'.", nameof(messages));
            }
        }

        return new ToolPromptHistory(history);
    }

    public static string BuildSystemPrompt(
        string? systemPrompt,
        IReadOnlyList<ToolDefinition> tools,
        int imageCount = 0,
        bool strictTools = false,
        bool allowRefusal = false)
    {
        return BuildSystemPrompt(
            systemPrompt,
            tools,
            new ToolPromptOptions
            {
                ToolChoice = ToolChoice.Auto,
                EnvelopeMode = ToolEnvelopeMode.StrictDeclared,
                ImageCount = imageCount,
                StrictTools = strictTools,
                AllowRefusal = allowRefusal,
            });
    }

    /// <summary>
    /// Builds the model-facing system prompt for an inferred or explicitly
    /// declared envelope.
    /// </summary>
    public static string BuildSystemPrompt(
        string? systemPrompt,
        IReadOnlyList<ToolDefinition> tools,
        ToolPromptOptions options)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.ToolChoice);

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            sb.Append(systemPrompt);
            sb.Append("\n\n");
        }

        if (options.ImageCount > 0)
        {
            sb.Append("## Visual context\n\n");
            sb.Append(options.ImageCount == 1
                ? "You have been shown 1 image in this turn. Refer to it naturally in your response.\n\n"
                : $"You have been shown {options.ImageCount} images in this turn, in the order they appear in the conversation. Refer to them naturally in your response.\n\n");
        }

        AppendEnvelopeInstructions(sb, options);

        if (tools.Count > 0)
        {
            sb.AppendLine("## Available tools");
            sb.AppendLine();

            foreach (var tool in tools)
            {
                sb.Append("### ");
                sb.AppendLine(tool.Name);
                sb.AppendLine(tool.Description ?? string.Empty);

                AppendToolParameters(sb, tool);

                sb.AppendLine();
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendEnvelopeInstructions(StringBuilder sb, ToolPromptOptions options)
    {
        var allowsMessage = options.ToolChoice.Mode is ToolChoiceMode.Auto or ToolChoiceMode.None;
        var allowsTools = options.ToolChoice.Mode is ToolChoiceMode.Auto or ToolChoiceMode.Required or ToolChoiceMode.Named;
        var allowsRefusal = options.AllowRefusal && allowsMessage;

        sb.AppendLine("## Tool calling");
        sb.AppendLine();
        sb.AppendLine("You must respond with one valid JSON object and no surrounding text.");
        sb.AppendLine();

        if (options.EnvelopeMode == ToolEnvelopeMode.Inferred)
        {
            if (allowsMessage)
            {
                sb.AppendLine("For a final answer:");
                sb.AppendLine("""{"text":"<your response here>"}""");
                sb.AppendLine();
            }

            if (allowsTools)
            {
                sb.AppendLine("When calling one or more tools:");
                sb.AppendLine("""{"tool_calls":[{"id":"call_<unique>","name":"<tool_name>","arguments":{<arguments>}}]}""");
                sb.AppendLine();
            }

            if (allowsRefusal)
            {
                sb.AppendLine("When declining to respond:");
                sb.AppendLine("""{"refusal":"<short reason>"}""");
                sb.AppendLine();
            }

            sb.AppendLine("Rules:");
            sb.AppendLine("- Use `text` for a final answer, `tool_calls` for one or more tool requests, or `refusal` when declining.");
            sb.AppendLine("- Every tool call has a unique string `id`, a known tool `name`, and an object-valued `arguments` field.");
            sb.AppendLine("- Do not include any text outside the JSON object.");
            sb.AppendLine();
            return;
        }

        if (allowsMessage)
        {
            sb.AppendLine("For a final answer:");
            sb.AppendLine("""{"mode":"message","text":"<your response here>","calls":[]}""");
            sb.AppendLine();
        }

        if (allowsTools)
        {
            sb.AppendLine("When calling one or more tools:");
            sb.AppendLine("""{"mode":"tool_calls","text":"","calls":[{"id":"call_<unique>","name":"<tool_name>","args":{<arguments>}}]}""");
            sb.AppendLine();
        }

        if (allowsRefusal)
        {
            sb.AppendLine("When declining to respond:");
            sb.AppendLine("""{"mode":"refusal","text":"<short reason>","calls":[]}""");
            sb.AppendLine();
        }

        sb.AppendLine("Rules:");
        sb.AppendLine("- `mode`, `text`, and `calls` are always present.");
        sb.AppendLine("- `args` is always a JSON object, never a JSON-encoded string.");
        sb.AppendLine("- Generate a unique `id` for each call.");
        sb.AppendLine("- Do not include any text outside the JSON object.");
        sb.AppendLine();
    }

    private static void AppendToolParameters(StringBuilder sb, ToolDefinition tool)
    {
        if (tool.ParametersSchema.ValueKind != JsonValueKind.Object
            || !tool.ParametersSchema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var required = new HashSet<string>(StringComparer.Ordinal);
        if (tool.ParametersSchema.TryGetProperty("required", out var requiredElement)
            && requiredElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in requiredElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } name)
                    required.Add(name);
            }
        }

        sb.AppendLine("Parameters:");
        foreach (var parameter in properties.EnumerateObject())
        {
            var typeName = "any";
            if (parameter.Value.TryGetProperty("type", out var typeElement)
                && typeElement.ValueKind == JsonValueKind.String)
            {
                typeName = typeElement.GetString() ?? "any";
            }

            var description = string.Empty;
            if (parameter.Value.TryGetProperty("description", out var descriptionElement)
                && descriptionElement.ValueKind == JsonValueKind.String)
            {
                description = descriptionElement.GetString() ?? string.Empty;
            }

            sb.Append("  - `");
            sb.Append(parameter.Name);
            sb.Append("` (");
            sb.Append(typeName);
            sb.Append(required.Contains(parameter.Name) ? ", required)" : ", optional)");
            if (!string.IsNullOrEmpty(description))
            {
                sb.Append(": ");
                sb.Append(description);
            }
            sb.AppendLine();
        }
    }

    public static string FormatAssistantMessage(string? content) =>
        JsonSerializer.Serialize(
            new { mode = "message", text = content ?? string.Empty, calls = Array.Empty<object>() },
            JsonOptions);

    public static string FormatAssistantToolCalls(ToolAwareMessage message)
    {
        if (message.ToolCalls is null)
            throw new ArgumentException("Assistant tool-call formatting requires ToolCalls.", nameof(message));

        var calls = message.ToolCalls.Select(toolCall => new
        {
            id = toolCall.Id,
            name = toolCall.Name,
            args = ParseArgsObject(toolCall.ArgumentsJson),
        });

        return JsonSerializer.Serialize(
            new { mode = "tool_calls", text = message.Content ?? string.Empty, calls },
            JsonOptions);
    }

    public static string FormatToolResult(ToolAwareMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.ToolCallId))
            throw new ArgumentException("Tool result formatting requires ToolCallId.", nameof(message));

        var content = message.Content ?? string.Empty;
        if (message.HasImage)
            content = $"[Image attached]\n{content}";

        return JsonSerializer.Serialize(
            new { tool_result = new { id = message.ToolCallId, content } },
            JsonOptions);
    }

    private static JsonElement ParseArgsObject(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            throw new ArgumentException("Tool call arguments must be a JSON object.", nameof(argumentsJson));

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Tool call arguments must be a JSON object.", nameof(argumentsJson));

            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("Tool call arguments must be a valid JSON object.", nameof(argumentsJson), ex);
        }
    }
}

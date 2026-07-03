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

        var history = new List<ToolPromptMessage>
        {
            new(
                ToolPromptRole.System,
                BuildSystemPrompt(systemPrompt, tools, imageCount, strictTools, allowRefusal))
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
        ArgumentNullException.ThrowIfNull(tools);

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            sb.Append(systemPrompt);
            sb.Append("\n\n");
        }

        if (imageCount > 0)
        {
            sb.Append("## Visual context\n\n");
            sb.Append(imageCount == 1
                ? "You have been shown 1 image in this turn. Refer to it naturally in your response.\n\n"
                : $"You have been shown {imageCount} images in this turn, in the order they appear in the conversation. Refer to them naturally in your response.\n\n");
        }

        sb.AppendLine("## Tool calling");
        sb.AppendLine();
        sb.AppendLine("You must respond with a JSON object in exactly this shape:");
        sb.AppendLine();
        sb.AppendLine("When responding with text only:");
        sb.AppendLine("""{"mode":"message","text":"<your response here>","calls":[]}""");
        sb.AppendLine();
        sb.AppendLine("When calling one or more tools:");
        sb.AppendLine("""{"mode":"tool_calls","text":"","calls":[{"id":"call_<unique>","name":"<tool_name>","args":{<arguments>}}]}""");
        sb.AppendLine();
        if (allowRefusal)
        {
            sb.AppendLine("When declining to respond:");
            sb.AppendLine("""{"mode":"refusal","text":"<short reason>","calls":[]}""");
            sb.AppendLine();
        }

        sb.AppendLine("Rules:");
        sb.AppendLine("- Every response must be valid JSON matching one of the shapes above.");
        sb.AppendLine(allowRefusal
            ? "- `mode` is one of: \"message\", \"tool_calls\", \"refusal\"."
            : "- `mode` is always either \"message\" or \"tool_calls\".");
        sb.AppendLine("- `text` is always a string.");
        sb.AppendLine("- `calls` is always an array.");
        sb.AppendLine("- `args` is always a JSON object, never a JSON-encoded string.");
        sb.AppendLine("- Generate a unique `id` for each call, such as `call_abc123`.");
        sb.AppendLine("- Do not include any text outside the JSON object.");
        sb.AppendLine();

        if (tools.Count > 0)
        {
            sb.AppendLine("## Available tools");
            sb.AppendLine();

            foreach (var tool in tools)
            {
                sb.Append("### ");
                sb.AppendLine(tool.Name);
                sb.AppendLine(tool.Description);

                if (!strictTools
                    && tool.ParametersSchema.ValueKind == JsonValueKind.Object
                    && tool.ParametersSchema.TryGetProperty("properties", out var properties)
                    && properties.ValueKind == JsonValueKind.Object)
                {
                    sb.AppendLine("Parameters:");
                    foreach (var parameter in properties.EnumerateObject())
                    {
                        var typeName = "any";
                        if (parameter.Value.TryGetProperty("type", out var typeElement))
                            typeName = typeElement.GetString() ?? "any";

                        var description = string.Empty;
                        if (parameter.Value.TryGetProperty("description", out var descriptionElement))
                            description = descriptionElement.GetString() ?? string.Empty;

                        sb.Append("  - `");
                        sb.Append(parameter.Name);
                        sb.Append("` (");
                        sb.Append(typeName);
                        sb.Append(')');
                        if (!string.IsNullOrEmpty(description))
                        {
                            sb.Append(": ");
                            sb.Append(description);
                        }
                        sb.AppendLine();
                    }
                }

                sb.AppendLine();
            }
        }

        return sb.ToString().TrimEnd();
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

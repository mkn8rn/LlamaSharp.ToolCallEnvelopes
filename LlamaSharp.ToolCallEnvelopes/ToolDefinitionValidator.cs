using System.Text.Json;

namespace LlamaSharp.ToolCallEnvelopes;

/// <summary>
/// Selects pre-inference validation checks for a dynamic tool catalog.
/// </summary>
public sealed record ToolDefinitionValidationOptions
{
    public bool RequireObjectRoot { get; init; } = true;
    public bool RequireKnownRequiredProperties { get; init; } = true;
    public bool RejectUnsupportedJsonSchemaKeywords { get; init; }
    public bool RequireAdditionalPropertiesFalse { get; init; }
}

/// <summary>
/// One validation problem found in a tool definition.
/// </summary>
public sealed record ToolDefinitionValidationError(
    string ToolName,
    string JsonPath,
    string Message);

/// <summary>
/// Result of validating a tool catalog before grammar generation.
/// </summary>
public sealed record ToolDefinitionValidationResult(
    bool Success,
    IReadOnlyList<ToolDefinitionValidationError> Errors)
{
    public void ThrowIfInvalid()
    {
        if (!Success)
        {
            throw new ArgumentException(
                "Tool definitions are invalid: " +
                string.Join("; ", Errors.Select(error =>
                    $"{error.ToolName} {error.JsonPath}: {error.Message}")));
        }
    }
}

/// <summary>
/// Validates the tool names and schema shape required by the envelope layer.
/// </summary>
public static class ToolDefinitionValidator
{
    public static ToolDefinitionValidationResult Validate(
        IReadOnlyList<ToolDefinition> tools,
        ToolDefinitionValidationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        options ??= new ToolDefinitionValidationOptions();

        var errors = new List<ToolDefinitionValidationError>();
        var names = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < tools.Count; index++)
        {
            var tool = tools[index];
            var toolName = tool.Name ?? string.Empty;
            var displayName = string.IsNullOrWhiteSpace(toolName) ? $"<tool[{index}]>" : toolName;

            if (string.IsNullOrWhiteSpace(toolName))
            {
                errors.Add(new(displayName, "$.name", "Tool name must be a non-empty string."));
            }
            else if (!names.Add(toolName))
            {
                errors.Add(new(displayName, "$.name", "Tool names must be unique."));
            }

            if (tool.ParametersSchema.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new(displayName, "$", "Parameter schema must be a JSON object."));
                continue;
            }

            if (options.RequireObjectRoot
                && tool.ParametersSchema.TryGetProperty("type", out var typeElement)
                && (typeElement.ValueKind != JsonValueKind.String
                    || !string.Equals(typeElement.GetString(), "object", StringComparison.Ordinal)))
            {
                errors.Add(new(displayName, "$.type", "Parameter schema root must have type 'object'."));
            }

            var properties = default(JsonElement);
            var hasProperties = tool.ParametersSchema.TryGetProperty("properties", out properties);
            if (hasProperties && properties.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new(displayName, "$.properties", "Properties must be a JSON object."));
                properties = default;
            }

            if (tool.ParametersSchema.TryGetProperty("required", out var requiredElement))
            {
                if (requiredElement.ValueKind != JsonValueKind.Array)
                {
                    errors.Add(new(displayName, "$.required", "Required must be a JSON array."));
                }
                else
                {
                    var known = hasProperties && properties.ValueKind == JsonValueKind.Object
                        ? properties.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal)
                        : new HashSet<string>(StringComparer.Ordinal);

                    var requiredIndex = 0;
                    foreach (var item in requiredElement.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.String)
                        {
                            errors.Add(new(displayName, $"$.required[{requiredIndex}]", "Required entries must be strings."));
                        }
                        else if (options.RequireKnownRequiredProperties
                                 && !known.Contains(item.GetString() ?? string.Empty))
                        {
                            errors.Add(new(
                                displayName,
                                $"$.required[{requiredIndex}]",
                                $"Required property '{item.GetString()}' is not declared in properties."));
                        }

                        requiredIndex++;
                    }
                }
            }

            if (options.RequireAdditionalPropertiesFalse
                && (!tool.ParametersSchema.TryGetProperty("additionalProperties", out var additional)
                    || additional.ValueKind != JsonValueKind.False))
            {
                errors.Add(new(
                    displayName,
                    "$.additionalProperties",
                    "additionalProperties must be false for this validation profile."));
            }

            if (options.RejectUnsupportedJsonSchemaKeywords)
            {
                var convertible = LlamaSharpJsonSchemaConverter.TryConvertFragment(
                    tool.ParametersSchema,
                    $"validation-{index}-",
                    out _,
                    out _,
                    out var unsupported);
                if (!convertible && unsupported.Count == 0)
                    unsupported = ["/ (schema conversion failed)"];

                errors.AddRange(unsupported.Select(keyword => new ToolDefinitionValidationError(
                    displayName,
                    keyword.Split(' ')[0],
                    $"Schema feature cannot be enforced: {keyword}.")));
            }
        }

        return new ToolDefinitionValidationResult(errors.Count == 0, errors);
    }
}

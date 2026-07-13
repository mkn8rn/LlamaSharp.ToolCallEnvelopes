using System.Text.Json;
using System.Text.RegularExpressions;
using LlamaSharp.ToolCallEnvelopes.Internal;

namespace LlamaSharp.ToolCallEnvelopes;

/// <summary>Describes one tool and its object-valued argument schema.</summary>
public sealed partial class ToolDefinition
{
    private ToolDefinition(string name, string description, JsonElement parameters)
    {
        Name = name;
        Description = description;
        Parameters = parameters;
    }

    /// <summary>The model-facing tool name.</summary>
    public string Name { get; }

    /// <summary>A short explanation of when the tool is useful.</summary>
    public string Description { get; }

    /// <summary>A cloned JSON Schema document for the tool arguments.</summary>
    public JsonElement Parameters { get; }

    /// <summary>Creates a definition and takes an independent copy of the schema.</summary>
    public static ToolDefinition Create(
        string name,
        string description,
        JsonElement parameters)
    {
        ValidateName(name, nameof(name));
        ValidateDescription(description, nameof(description));

        try
        {
            if (parameters.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException(
                    $"The tool parameter schema must be a JSON object, but the supplied "
                    + $"JsonElement is {parameters.ValueKind}. Supply an object-valued schema with "
                    + "an explicit root type of 'object' and additionalProperties set to false.",
                    nameof(parameters));
            }

            return new ToolDefinition(name, description, parameters.Clone());
        }
        catch (InvalidOperationException exception)
        {
            throw new ArgumentException(
                "The tool parameter schema cannot be read because its owning JsonDocument has "
                + "already been disposed. Call ToolDefinition.Create while the document is open, "
                + "or pass the schema text to ToolDefinition.Parse; both methods retain an "
                + "independent copy.",
                nameof(parameters),
                exception);
        }
    }

    /// <summary>Parses a JSON Schema string into an independently owned definition.</summary>
    public static ToolDefinition Parse(
        string name,
        string description,
        string parametersJson)
    {
        Guard.NotNull(
            parametersJson,
            nameof(parametersJson),
            "The tool parameter schema text cannot be null. Supply one complete JSON object with "
            + "an explicit root type of 'object' and additionalProperties set to false.");

        try
        {
            using var document = JsonDocument.Parse(parametersJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 256,
            });

            return Create(name, description, document.RootElement);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                "The tool parameter schema text is not one complete strict JSON object. Remove "
                + "comments and trailing commas, correct the JSON syntax identified by the inner "
                + "JsonException, and supply an object-valued schema with root type 'object'.",
                nameof(parametersJson),
                exception);
        }
    }

    internal static void ValidateName(string name, string parameterName)
    {
        Guard.NotNull(
            name,
            parameterName,
            "A tool name is required because the grammar, parser, dispatcher, and history all use "
            + "that exact name for correlation.");
        if (!ToolNamePattern().IsMatch(name))
        {
            throw new ArgumentException(
                $"The tool name is invalid. Expected [A-Za-z_][A-Za-z0-9_.-]{{0,63}}, but "
                + $"received {name.Length} character(s). Start with an ASCII letter or underscore, "
                + "then use only ASCII letters, digits, underscore, dot, or hyphen.",
                parameterName);
        }
    }

    internal static void ValidateDescription(string description, string parameterName)
    {
        Guard.NotNull(
            description,
            parameterName,
            "A concise tool description is required so the model can decide when the tool is "
            + "appropriate. Supply a short sentence that explains when to use the tool.");
        if (string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException(
                $"The tool description contains {description.Length} character(s) but no visible "
                + "text. Supply a concise sentence that tells the model when to use the tool.",
                parameterName);
        }

        var controlIndex = Array.FindIndex(description.ToCharArray(), char.IsControl);
        if (controlIndex >= 0)
        {
            throw new ArgumentException(
                $"The tool description contains control character U+{(int)description[controlIndex]:X4} "
                + $"at index {controlIndex}. Replace control characters with ordinary spaces or "
                + "printable text before compiling the model-facing catalog.",
                parameterName);
        }
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ToolNamePattern();
}

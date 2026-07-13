using System.Globalization;
using System.Text.Json;

namespace LlamaSharp.ToolCallEnvelopes.Internal;

internal sealed class SchemaKeywordReader
{
    private const string SupportedDialect =
        "https://json-schema.org/draft/2020-12/schema";

    private static readonly HashSet<string> AnnotationKeywords = new(StringComparer.Ordinal)
    {
        "$comment",
        "default",
        "deprecated",
        "description",
        "examples",
        "readOnly",
        "title",
        "writeOnly",
    };

    private readonly SchemaCompilationContext _context;

    internal SchemaKeywordReader(SchemaCompilationContext context) => _context = context;

    internal void ValidateReferenceKeywords(JsonElement schema, string pointer)
    {
        foreach (var property in schema.EnumerateObject())
        {
            if (property.Name != "$ref"
                && property.Name != "$schema"
                && !AnnotationKeywords.Contains(property.Name))
            {
                _context.Add(
                    ToolEnvelopePlanDiagnosticCode.UnsupportedKeyword,
                    "A reference node may contain only $ref and annotation keywords.",
                    JsonPointers.Property(pointer, property.Name));
            }
        }
    }

    internal void ValidateDialect(JsonElement schema, string pointer)
    {
        if (!schema.TryGetProperty("$schema", out var dialect))
            return;

        var dialectPointer = JsonPointers.Property(pointer, "$schema");
        if (!string.IsNullOrEmpty(pointer))
        {
            _context.Add(
                ToolEnvelopePlanDiagnosticCode.UnsupportedKeyword,
                "A $schema declaration is accepted only at the tool schema root. Remove this "
                + "nested declaration so every node uses the plan's single supported dialect.",
                dialectPointer);
            return;
        }

        if (dialect.ValueKind != JsonValueKind.String
            || !string.Equals(dialect.GetString(), SupportedDialect, StringComparison.Ordinal))
        {
            _context.Add(
                ToolEnvelopePlanDiagnosticCode.UnsupportedKeyword,
                $"The root $schema field must be the exact string '{SupportedDialect}'. Change "
                + "the declaration to that dialect or remove it before compiling this bounded "
                + "tool schema profile.",
                dialectPointer);
        }
    }

    internal HashSet<string> ReadRequiredNames(JsonElement schema, string pointer)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (!schema.TryGetProperty("required", out var required))
            return names;

        if (required.ValueKind != JsonValueKind.Array)
        {
            _context.Add(
                ToolEnvelopePlanDiagnosticCode.InvalidRequiredProperty,
                "required must be an array of unique strings.",
                JsonPointers.Property(pointer, "required"));
            return names;
        }

        var index = 0;
        foreach (var value in required.EnumerateArray())
        {
            var itemPointer = JsonPointers.Index(JsonPointers.Property(pointer, "required"), index++);
            if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } name)
            {
                _context.Add(
                    ToolEnvelopePlanDiagnosticCode.InvalidRequiredProperty,
                    "Every required entry must be a string.",
                    itemPointer);
            }
            else if (!names.Add(name))
            {
                _context.Add(
                    ToolEnvelopePlanDiagnosticCode.InvalidRequiredProperty,
                    $"Required property '{name}' is repeated.",
                    itemPointer);
            }
        }

        return names;
    }

    internal IReadOnlyList<JsonElement> ReadEnumeration(
        JsonElement schema,
        SchemaKind kind,
        string pointer)
    {
        if (!schema.TryGetProperty("enum", out var enumeration))
            return [];

        if (enumeration.ValueKind != JsonValueKind.Array)
        {
            _context.Add(
                ToolEnvelopePlanDiagnosticCode.InvalidConstraint,
                "enum must be a non-empty array.",
                JsonPointers.Property(pointer, "enum"));
            return [];
        }

        var values = enumeration.EnumerateArray().ToArray();
        if (values.Length == 0)
        {
            _context.Add(
                ToolEnvelopePlanDiagnosticCode.InvalidConstraint,
                "enum must contain at least one value.",
                JsonPointers.Property(pointer, "enum"));
            return [];
        }

        if (values.Length > _context.Limits.MaxEnumValues)
        {
            _context.Add(
                ToolEnvelopePlanDiagnosticCode.TooManyEnumValues,
                $"enum has {values.Length} values; the plan limit is "
                + $"{_context.Limits.MaxEnumValues}.",
                JsonPointers.Property(pointer, "enum"));
        }

        var textCharacters = values.Sum(value => value.GetRawText().Length);
        if (textCharacters > _context.Limits.MaxEnumTextCharacters)
        {
            _context.Add(
                ToolEnvelopePlanDiagnosticCode.EnumTextTooLong,
                $"enum text uses {textCharacters} characters; the plan limit is "
                + $"{_context.Limits.MaxEnumTextCharacters}.",
                JsonPointers.Property(pointer, "enum"));
        }

        for (var index = 0; index < values.Length; index++)
        {
            if (!ValueMatchesKind(values[index], kind))
            {
                _context.Add(
                    ToolEnvelopePlanDiagnosticCode.InvalidConstraint,
                    $"The enum value does not match declared type {SchemaKinds.JsonName(kind)}.",
                    JsonPointers.Index(JsonPointers.Property(pointer, "enum"), index));
            }

            var duplicateIndex = Array.FindIndex(
                values,
                0,
                index,
                candidate => SchemaValidator.ValuesEqual(candidate, values[index]));
            if (duplicateIndex >= 0)
            {
                _context.Add(
                    ToolEnvelopePlanDiagnosticCode.InvalidConstraint,
                    $"The enum value repeats the value at index {duplicateIndex}; enum values "
                    + "must be unique.",
                    JsonPointers.Index(JsonPointers.Property(pointer, "enum"), index));
            }
        }

        return Array.AsReadOnly(values.Select(value => value.Clone()).ToArray());
    }

    internal JsonElement? ReadConstant(JsonElement schema, SchemaKind kind, string pointer)
    {
        if (!schema.TryGetProperty("const", out var constant))
            return null;

        if (!ValueMatchesKind(constant, kind))
        {
            _context.Add(
                ToolEnvelopePlanDiagnosticCode.InvalidConstraint,
                $"The const value does not match declared type {SchemaKinds.JsonName(kind)}.",
                JsonPointers.Property(pointer, "const"));
        }

        return constant.Clone();
    }

    internal void ValidateConstantAgainstEnumeration(
        JsonElement? constant,
        IReadOnlyList<JsonElement> enumeration,
        string pointer)
    {
        if (constant is null || enumeration.Count == 0)
            return;

        if (!enumeration.Any(value => SchemaValidator.ValuesEqual(value, constant.Value)))
        {
            _context.Add(
                ToolEnvelopePlanDiagnosticCode.InvalidConstraint,
                "const must also be one of the declared enum values.",
                pointer);
        }
    }

    internal string? ReadDescription(JsonElement schema, string pointer)
    {
        if (!schema.TryGetProperty("description", out var description))
            return null;

        if (description.ValueKind != JsonValueKind.String)
        {
            _context.Add(
                ToolEnvelopePlanDiagnosticCode.InvalidSchema,
                "description must be a string.",
                JsonPointers.Property(pointer, "description"));
            return null;
        }

        var value = description.GetString()!;
        if (value.Length > _context.Limits.MaxParameterDescriptionCharacters)
        {
            _context.Add(
                ToolEnvelopePlanDiagnosticCode.DescriptionTooLong,
                $"The parameter description has {value.Length} characters; the plan limit is "
                + $"{_context.Limits.MaxParameterDescriptionCharacters}.",
                JsonPointers.Property(pointer, "description"));
        }

        if (value.Any(char.IsControl))
        {
            _context.Add(
                ToolEnvelopePlanDiagnosticCode.InvalidSchema,
                "Parameter descriptions cannot contain control characters.",
                JsonPointers.Property(pointer, "description"));
        }

        return value;
    }

    internal bool TryReadKind(JsonElement schema, string pointer, out SchemaKind kind)
    {
        kind = default;
        if (!schema.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
        {
            _context.Add(
                ToolEnvelopePlanDiagnosticCode.InvalidSchema,
                "Every schema node must declare one explicit string-valued type.",
                JsonPointers.Property(pointer, "type"));
            return false;
        }

        var name = type.GetString();
        var known = name switch
        {
            "object" => SchemaKind.Object,
            "array" => SchemaKind.Array,
            "string" => SchemaKind.String,
            "integer" => SchemaKind.Integer,
            "number" => SchemaKind.Number,
            "boolean" => SchemaKind.Boolean,
            "null" => SchemaKind.Null,
            _ => (SchemaKind?)null,
        };

        if (known is null)
        {
            _context.Add(
                ToolEnvelopePlanDiagnosticCode.InvalidSchema,
                $"Unsupported schema type '{name}'.",
                JsonPointers.Property(pointer, "type"));
            return false;
        }

        kind = known.Value;
        return true;
    }

    internal void ValidateKeywords(JsonElement schema, SchemaKind kind, string pointer)
    {
        var allowed = new HashSet<string>(AnnotationKeywords, StringComparer.Ordinal)
        {
            "$defs",
            "$schema",
            "type",
        };

        switch (kind)
        {
            case SchemaKind.Object:
                allowed.UnionWith(["properties", "required", "additionalProperties"]);
                break;
            case SchemaKind.Array:
                allowed.UnionWith(["items", "minItems", "maxItems"]);
                break;
            case SchemaKind.String:
                allowed.UnionWith(["minLength", "maxLength"]);
                break;
            case SchemaKind.Integer:
            case SchemaKind.Number:
                allowed.UnionWith(["minimum", "maximum"]);
                break;
        }

        if (kind is not (SchemaKind.Object or SchemaKind.Array))
            allowed.UnionWith(["enum", "const"]);

        foreach (var property in schema.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                _context.Add(
                    ToolEnvelopePlanDiagnosticCode.UnsupportedKeyword,
                    $"Schema keyword '{property.Name}' is outside the supported tool profile.",
                    JsonPointers.Property(pointer, property.Name));
            }
        }
    }

    internal int ReadNonNegativeInteger(
        JsonElement schema,
        string propertyName,
        string pointer,
        int defaultValue)
    {
        if (!schema.TryGetProperty(propertyName, out var value))
            return defaultValue;

        if (!value.TryGetInt32(out var number) || number < 0)
        {
            _context.Add(
                ToolEnvelopePlanDiagnosticCode.InvalidConstraint,
                $"{propertyName} must be a non-negative 32-bit integer.",
                JsonPointers.Property(pointer, propertyName));
            return defaultValue;
        }

        return number;
    }

    internal decimal? ReadDecimal(JsonElement schema, string propertyName, string pointer)
    {
        if (!schema.TryGetProperty(propertyName, out var value))
            return null;

        if (!TryReadNumber(value, out var number))
        {
            _context.Add(
                ToolEnvelopePlanDiagnosticCode.InvalidConstraint,
                $"{propertyName} must be a finite number in the .NET decimal range.",
                JsonPointers.Property(pointer, propertyName));
            return null;
        }

        return number;
    }

    internal bool CheckUniqueProperties(JsonElement schema, string pointer)
    {
        var unique = true;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in schema.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                unique = false;
                _context.Add(
                    ToolEnvelopePlanDiagnosticCode.DuplicateSchemaProperty,
                    $"Schema property '{property.Name}' is repeated.",
                    JsonPointers.Property(pointer, property.Name));
            }
        }

        return unique;
    }

    internal static bool TryReadNumber(JsonElement value, out decimal number)
    {
        number = default;
        return value.ValueKind == JsonValueKind.Number
               && decimal.TryParse(
                   value.GetRawText(),
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out number);
    }

    private static bool ValueMatchesKind(JsonElement value, SchemaKind kind) => kind switch
    {
        SchemaKind.Object => value.ValueKind == JsonValueKind.Object,
        SchemaKind.Array => value.ValueKind == JsonValueKind.Array,
        SchemaKind.String => value.ValueKind == JsonValueKind.String,
        SchemaKind.Integer => TryReadNumber(value, out var number)
                              && decimal.Truncate(number) == number,
        SchemaKind.Number => TryReadNumber(value, out _),
        SchemaKind.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        SchemaKind.Null => value.ValueKind == JsonValueKind.Null,
        _ => false,
    };

}

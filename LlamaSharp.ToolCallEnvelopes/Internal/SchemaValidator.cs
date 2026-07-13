using System.Globalization;
using System.Text;
using System.Text.Json;

namespace LlamaSharp.ToolCallEnvelopes.Internal;

internal static class SchemaValidator
{
    internal static bool TryValidate(
        SchemaNode schema,
        JsonElement value,
        string pointer,
        ToolEnvelopeLimits limits,
        out SchemaViolation? violation)
    {
        if (!KindMatches(schema.Kind, value))
        {
            violation = new SchemaViolation(
                pointer,
                $"Expected {KindName(schema.Kind)} but found {ValueKindName(value)}.");
            return false;
        }

        if (schema.Constant is { } constant && !ValuesEqual(value, constant))
        {
            violation = new SchemaViolation(pointer, "The value does not match const.");
            return false;
        }

        if (schema.EnumValues.Count > 0
            && !schema.EnumValues.Any(candidate => ValuesEqual(value, candidate)))
        {
            violation = new SchemaViolation(pointer, "The value is not one of the allowed enum values.");
            return false;
        }

        switch (schema.Kind)
        {
            case SchemaKind.Object:
                return ValidateObject(schema, value, pointer, limits, out violation);
            case SchemaKind.Array:
                return ValidateArray(schema, value, pointer, limits, out violation);
            case SchemaKind.String:
                return ValidateString(schema, value, pointer, out violation);
            case SchemaKind.Integer:
            case SchemaKind.Number:
                return ValidateNumber(schema, value, pointer, limits, out violation);
            case SchemaKind.Boolean:
            case SchemaKind.Null:
                violation = null;
                return true;
            default:
                throw new InvalidOperationException(
                    $"Compiled schema node {schema.Id} at '{schema.Pointer}' has unsupported kind "
                    + $"'{schema.Kind}' during post-generation validation. This indicates a package "
                    + "defect because every admitted kind must have a validator. The call was not "
                    + "dispatched; report the plan and response to the package maintainer.");
        }
    }

    internal static bool ValuesEqual(JsonElement left, JsonElement right)
    {
        if (left.ValueKind == JsonValueKind.Number && right.ValueKind == JsonValueKind.Number)
        {
            return decimal.TryParse(
                       left.GetRawText(),
                       NumberStyles.Float,
                       CultureInfo.InvariantCulture,
                       out var leftNumber)
                   && decimal.TryParse(
                       right.GetRawText(),
                       NumberStyles.Float,
                       CultureInfo.InvariantCulture,
                       out var rightNumber)
                   && leftNumber == rightNumber;
        }

        if (left.ValueKind != right.ValueKind)
            return false;

        return left.ValueKind switch
        {
            JsonValueKind.String => string.Equals(
                left.GetString(), right.GetString(), StringComparison.Ordinal),
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            _ => string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal),
        };
    }

    private static bool ValidateObject(
        SchemaNode schema,
        JsonElement value,
        string pointer,
        ToolEnvelopeLimits limits,
        out SchemaViolation? violation)
    {
        var propertySchemas = schema.Properties.ToDictionary(
            property => property.Name,
            StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in value.EnumerateObject())
        {
            var propertyPointer = JsonPointers.Property(pointer, property.Name);
            if (!seen.Add(property.Name))
            {
                violation = new SchemaViolation(
                    propertyPointer,
                    $"Property '{property.Name}' is repeated.");
                return false;
            }

            if (!propertySchemas.TryGetValue(property.Name, out var compiledProperty))
            {
                violation = new SchemaViolation(
                    propertyPointer,
                    $"Property '{property.Name}' is not allowed.");
                return false;
            }

            if (!TryValidate(
                    compiledProperty.Schema,
                    property.Value,
                    propertyPointer,
                    limits,
                    out violation))
            {
                return false;
            }
        }

        foreach (var required in schema.Properties.Where(property => property.IsRequired))
        {
            if (!seen.Contains(required.Name))
            {
                violation = new SchemaViolation(
                    JsonPointers.Property(pointer, required.Name),
                    $"Required property '{required.Name}' is missing.");
                return false;
            }
        }

        violation = null;
        return true;
    }

    private static bool ValidateArray(
        SchemaNode schema,
        JsonElement value,
        string pointer,
        ToolEnvelopeLimits limits,
        out SchemaViolation? violation)
    {
        var count = value.GetArrayLength();
        if (count < schema.MinimumItems || count > schema.MaximumItems)
        {
            violation = new SchemaViolation(
                pointer,
                $"Expected between {schema.MinimumItems} and {schema.MaximumItems} array items; "
                + $"found {count}.");
            return false;
        }

        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (!TryValidate(
                    schema.Items!,
                    item,
                    JsonPointers.Index(pointer, index++),
                    limits,
                    out violation))
            {
                return false;
            }
        }

        violation = null;
        return true;
    }

    private static bool ValidateString(
        SchemaNode schema,
        JsonElement value,
        string pointer,
        out SchemaViolation? violation)
    {
        if (!JsonStringUnicode.TryValidate(value, out var unicodeProblem))
        {
            violation = new SchemaViolation(pointer, unicodeProblem!);
            return false;
        }

        var text = value.GetString()!;
        var length = text.EnumerateRunes().Count();
        if (length < schema.MinimumLength || length > schema.MaximumLength)
        {
            violation = new SchemaViolation(
                pointer,
                $"Expected between {schema.MinimumLength} and {schema.MaximumLength} Unicode "
                + $"characters; found {length}.");
            return false;
        }

        violation = null;
        return true;
    }

    private static bool ValidateNumber(
        SchemaNode schema,
        JsonElement value,
        string pointer,
        ToolEnvelopeLimits limits,
        out SchemaViolation? violation)
    {
        var raw = value.GetRawText();
        if (raw.Length > limits.MaxGeneratedNumberCharacters
            || !decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            violation = new SchemaViolation(
                pointer,
                $"The number must fit within {limits.MaxGeneratedNumberCharacters} characters "
                + "and the .NET decimal range.");
            return false;
        }

        if (schema.Kind == SchemaKind.Integer && decimal.Truncate(number) != number)
        {
            violation = new SchemaViolation(pointer, "Expected an integer value.");
            return false;
        }

        if (schema.Minimum is not null && number < schema.Minimum)
        {
            violation = new SchemaViolation(
                pointer,
                $"The value is below the minimum {schema.Minimum.Value.ToString(CultureInfo.InvariantCulture)}.");
            return false;
        }

        if (schema.Maximum is not null && number > schema.Maximum)
        {
            violation = new SchemaViolation(
                pointer,
                $"The value is above the maximum {schema.Maximum.Value.ToString(CultureInfo.InvariantCulture)}.");
            return false;
        }

        violation = null;
        return true;
    }

    private static bool KindMatches(SchemaKind kind, JsonElement value) => kind switch
    {
        SchemaKind.Object => value.ValueKind == JsonValueKind.Object,
        SchemaKind.Array => value.ValueKind == JsonValueKind.Array,
        SchemaKind.String => value.ValueKind == JsonValueKind.String,
        SchemaKind.Integer or SchemaKind.Number => value.ValueKind == JsonValueKind.Number,
        SchemaKind.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        SchemaKind.Null => value.ValueKind == JsonValueKind.Null,
        _ => false,
    };

    private static string KindName(SchemaKind kind) => kind switch
    {
        SchemaKind.Object => "an object",
        SchemaKind.Array => "an array",
        SchemaKind.String => "a string",
        SchemaKind.Integer => "an integer",
        SchemaKind.Number => "a number",
        SchemaKind.Boolean => "a boolean",
        SchemaKind.Null => "null",
        _ => kind.ToString(),
    };

    private static string ValueKindName(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "an array",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Null => "null",
        _ => value.ValueKind.ToString(),
    };
}

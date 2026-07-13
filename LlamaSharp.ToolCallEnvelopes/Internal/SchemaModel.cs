using System.Text.Json;
using System.Text;

namespace LlamaSharp.ToolCallEnvelopes.Internal;

internal enum SchemaKind
{
    Object,
    Array,
    String,
    Integer,
    Number,
    Boolean,
    Null,
}

internal static class SchemaKinds
{
    internal static string JsonName(SchemaKind kind) => kind switch
    {
        SchemaKind.Object => "object",
        SchemaKind.Array => "array",
        SchemaKind.String => "string",
        SchemaKind.Integer => "integer",
        SchemaKind.Number => "number",
        SchemaKind.Boolean => "boolean",
        SchemaKind.Null => "null",
        _ => throw new ArgumentOutOfRangeException(
            nameof(kind),
            kind,
            "The schema kind has no JSON Schema type name. This indicates a package defect "
            + "because every compiled schema kind must have one model-facing name."),
    };
}

internal sealed record SchemaNode
{
    internal required int Id { get; init; }
    internal required string Pointer { get; init; }
    internal required SchemaKind Kind { get; init; }
    internal required int InstanceDepth { get; init; }
    internal string? Description { get; init; }
    internal IReadOnlyList<SchemaProperty> Properties { get; init; } = [];
    internal SchemaNode? Items { get; init; }
    internal int MinimumItems { get; init; }
    internal int MaximumItems { get; init; }
    internal int MinimumLength { get; init; }
    internal int MaximumLength { get; init; }
    internal decimal? Minimum { get; init; }
    internal decimal? Maximum { get; init; }
    internal IReadOnlyList<JsonElement> EnumValues { get; init; } = [];
    internal JsonElement? Constant { get; init; }
}

internal sealed record SchemaProperty(
    string Name,
    SchemaNode Schema,
    bool IsRequired,
    string Pointer);

internal sealed record CompiledTool(
    ToolDefinition Definition,
    SchemaNode Arguments,
    int SchemaRuleCount,
    int MaximumSchemaDepth);

internal sealed record SchemaViolation(string JsonPointer, string Message);

internal static class JsonPointers
{
    internal static string Property(string parent, string name) =>
        $"{parent}/{Escape(name)}";

    internal static string Index(string parent, int index) => $"{parent}/{index}";

    internal static string Escape(string value) =>
        value.Replace("~", "~0", StringComparison.Ordinal)
            .Replace("/", "~1", StringComparison.Ordinal);

    internal static bool TryUnescape(string value, out string decoded)
    {
        var output = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character != '~')
            {
                output.Append(character);
                continue;
            }

            if (++index >= value.Length || value[index] is not ('0' or '1'))
            {
                decoded = string.Empty;
                return false;
            }

            output.Append(value[index] == '0' ? '~' : '/');
        }

        decoded = output.ToString();
        return true;
    }
}

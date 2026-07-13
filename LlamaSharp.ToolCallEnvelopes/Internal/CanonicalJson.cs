using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LlamaSharp.ToolCallEnvelopes.Internal;

internal static class CanonicalJson
{
    internal static string SerializeAssistantMessage(string text) =>
        Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("text", text);
            writer.WriteEndObject();
        });

    internal static string SerializeRefusal(string reason) =>
        Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("refusal", reason);
            writer.WriteEndObject();
        });

    internal static string SerializeToolRequest(IReadOnlyList<ToolCall> calls) =>
        Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartArray("tool_calls");
            foreach (var call in calls)
            {
                writer.WriteStartObject();
                writer.WriteString("name", call.Name);
                writer.WritePropertyName("arguments");
                call.Arguments.WriteTo(writer);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });

    internal static string SerializeToolResult(ToolCall call, string content) =>
        Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartObject("tool_result");
            writer.WriteNumber("call_index", call.Index);
            writer.WriteString("name", call.Name);
            writer.WriteString("content", content);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });

    internal static string SerializeString(string value) => JsonSerializer.Serialize(value);

    internal static string ComputeCatalogFingerprint(
        IReadOnlyList<ToolDefinition> tools,
        ToolEnvelopePlanOptions options)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("allow_refusal", options.AllowRefusal);
            writer.WriteNumber("max_calls_per_turn", options.MaxCallsPerTurn);
            WriteLimits(writer, options.Limits);
            writer.WriteStartArray("tools");
            foreach (var tool in tools)
            {
                writer.WriteStartObject();
                writer.WriteString("name", tool.Name);
                writer.WriteString("description", tool.Description);
                writer.WritePropertyName("parameters");
                WriteCanonical(writer, tool.Parameters);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan));
    }

    internal static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(
                             property => property.Name,
                             StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;

            default:
                value.WriteTo(writer);
                break;
        }
    }

    private static string Write(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            write(writer);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteLimits(Utf8JsonWriter writer, ToolEnvelopeLimits limits)
    {
        writer.WriteStartObject("limits");
        foreach (var property in typeof(ToolEnvelopeLimits)
                     .GetProperties()
                     .Where(property => property.PropertyType == typeof(int))
                     .OrderBy(property => property.Name, StringComparer.Ordinal))
        {
            writer.WriteNumber(property.Name, (int)property.GetValue(limits)!);
        }

        writer.WriteEndObject();
    }
}

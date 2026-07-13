using System.Text.Json;

namespace LlamaSharp.ToolCallEnvelopes.Tests;

internal static class TestCatalog
{
    internal const string WeatherSchema =
        """
        {
          "type": "object",
          "properties": {
            "city": {
              "type": "string",
              "minLength": 1,
              "maxLength": 64,
              "description": "City or region."
            },
            "unit": {
              "type": "string",
              "enum": ["celsius", "fahrenheit"],
              "description": "Temperature unit."
            },
            "days": {
              "type": "integer",
              "minimum": 1,
              "maximum": 7
            },
            "include_alerts": {
              "type": "boolean"
            },
            "tags": {
              "type": "array",
              "items": { "type": "string", "maxLength": 20 },
              "minItems": 0,
              "maxItems": 3
            },
            "coordinates": {
              "type": "object",
              "properties": {
                "latitude": { "type": "number", "minimum": -90, "maximum": 90 },
                "longitude": { "type": "number", "minimum": -180, "maximum": 180 }
              },
              "required": ["latitude", "longitude"],
              "additionalProperties": false
            }
          },
          "required": ["city", "unit"],
          "additionalProperties": false
        }
        """;

    internal const string SearchSchema =
        """
        {
          "type": "object",
          "properties": {
            "query": { "type": "string", "minLength": 1, "maxLength": 128 },
            "limit": { "type": "integer", "minimum": 1, "maximum": 10 }
          },
          "required": ["query"],
          "additionalProperties": false
        }
        """;

    internal static ToolDefinition Weather(string description = "Gets current weather for one city.") =>
        ToolDefinition.Parse("get_weather", description, WeatherSchema);

    internal static ToolDefinition Search() =>
        ToolDefinition.Parse("search", "Searches the local index.", SearchSchema);

    internal static ToolEnvelopePlan Plan(
        bool allowRefusal = false,
        int maxCalls = 1,
        ToolEnvelopeLimits? limits = null,
        IEnumerable<ToolDefinition>? tools = null) =>
        ToolEnvelopePlan.Compile(
            tools ?? [Weather()],
            new ToolEnvelopePlanOptions
            {
                AllowRefusal = allowRefusal,
                MaxCallsPerTurn = maxCalls,
                Limits = limits ?? ToolEnvelopeLimits.Constrained,
            });

    internal static ToolEnvelopeTurn Turn(
        ToolChoice? choice = null,
        bool allowRefusal = false,
        int maxCalls = 1,
        IEnumerable<ToolDefinition>? tools = null,
        ToolEnvelopeLimits? limits = null) =>
        Plan(allowRefusal, maxCalls, limits, tools).CreateTurn(
            "Use tools only when the turn permits them.",
            [ToolMessage.User("What is the weather in Zagreb?")],
            choice ?? ToolChoice.Auto);

    internal static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    internal static string ToolRequest(string name, string arguments) =>
        $$"""{"tool_calls":[{"name":{{JsonSerializer.Serialize(name)}},"arguments":{{arguments}}}]}""";
}

# Tool schema profile

The package accepts one bounded JSON Schema profile for tool arguments. Plan
compilation either represents the schema and validates it end to end or rejects
it with `ToolEnvelopePlanException`. There is no permissive object substitute.

Every node declares exactly one string-valued `type`. The accepted types are
`object`, `array`, `string`, `integer`, `number`, `boolean`, and `null`.
Objects use `properties`, optional `required`, and
`additionalProperties: false`. Arrays use one `items` schema with optional
`minItems` and `maxItems`. Strings use optional `minLength` and `maxLength`.
Numbers use inclusive `minimum` and `maximum`. Primitive nodes may use `enum`
and `const`. Acyclic local `$ref` values use JSON Pointer syntax beginning with
`#/`.

The root may use the standard 2020-12 `$schema` declaration shown below. Other
dialects, nested dialect declarations, `$id`, and `$anchor` are rejected
because the plan uses one dialect and one local JSON Pointer document for every
compiled tool.

Every `$defs` entry is validated even when no current property references it.
Unsupported or recursive definitions cannot hide in an otherwise usable plan.

This is a complete example.

```json
{
  "type": "object",
  "$defs": {
    "coordinates": {
      "type": "object",
      "properties": {
        "latitude": {
          "type": "number",
          "minimum": -90,
          "maximum": 90
        },
        "longitude": {
          "type": "number",
          "minimum": -180,
          "maximum": 180
        }
      },
      "required": ["latitude", "longitude"],
      "additionalProperties": false
    }
  },
  "properties": {
    "city": {
      "type": "string",
      "minLength": 1,
      "maxLength": 64
    },
    "unit": {
      "type": "string",
      "enum": ["celsius", "fahrenheit"]
    },
    "coordinates": {
      "$ref": "#/$defs/coordinates"
    }
  },
  "required": ["city", "unit"],
  "additionalProperties": false
}
```

Composition keywords, recursive references, open objects, regular-expression
patterns, formats, conditional schemas, tuple arrays, exclusive numeric
bounds, and unknown validation keywords are rejected. This avoids promising an
approximation that differs between prompting, grammar generation, and final
validation.

The grammar deliberately generates one deterministic, bounded structural
shape. Required properties appear first, optional properties appear at most
once in schema order, whitespace is compact, tool names are literal terminals,
and every repetition is bounded. Inclusive numeric ranges and the aggregate
envelope limit are checked by the completed parser because encoding arbitrary
decimal intervals or a cross-field character sum in GBNF would make sampling
larger and less predictable. The completed parser remains property-order
independent and enforces every admitted constraint before a call reaches
application code.

When a schema omits a string or array maximum, the plan's resource maximum
applies. When the schema declares a larger maximum, the tighter plan maximum
applies. Numbers must fit the configured text bound and the .NET `decimal`
range. String lengths count Unicode scalar values.

`ToolEnvelopeLimits.Constrained` is designed for small local-model contexts and
bounded host memory. Override an individual limit with a record expression
when the model and application can afford it.

```csharp
var limits = ToolEnvelopeLimits.Constrained with
{
    MaxTools = 8,
    MaxCatalogPromptCharacters = 3_000,
    MaxGeneratedStringCharacters = 512,
    MaxToolResultCharacters = 8_192,
};

var plan = ToolEnvelopePlan.Compile(
    tools,
    new ToolEnvelopePlanOptions
    {
        MaxCallsPerTurn = 2,
        AllowRefusal = true,
        Limits = limits,
    });
```

Tool names match `[A-Za-z_][A-Za-z0-9_.-]{0,63}`. Tool and parameter
descriptions cannot contain raw control characters. Catalog, schema,
description, rule, depth, enum, output, result, number, string, array, and
diagnostic sizes are all checked before or during use.

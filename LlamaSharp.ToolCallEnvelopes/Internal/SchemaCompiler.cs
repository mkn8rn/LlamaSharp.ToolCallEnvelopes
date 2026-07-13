using System.Globalization;
using System.Text;
using System.Text.Json;

namespace LlamaSharp.ToolCallEnvelopes.Internal;

internal sealed class SchemaCompiler
{
    private readonly SchemaCompilationContext _context;
    private readonly SchemaKeywordReader _keywords;
    private readonly Dictionary<string, SchemaNode> _compiledByPointer = new(StringComparer.Ordinal);
    private readonly HashSet<string> _activePointers = new(StringComparer.Ordinal);
    private int _nextNodeId;
    private int _maximumDepth;

    internal SchemaCompiler(
        ToolDefinition tool,
        ToolEnvelopeLimits limits,
        List<ToolEnvelopePlanDiagnostic> diagnostics)
    {
        _context = new SchemaCompilationContext(tool, limits, diagnostics);
        _keywords = new SchemaKeywordReader(_context);
    }

    private ToolDefinition Tool => _context.Tool;
    private ToolEnvelopeLimits Limits => _context.Limits;

    internal CompiledTool? Compile()
    {
        var before = _context.DiagnosticCount;
        var root = CompileNode(Tool.Parameters, string.Empty, depth: 1);
        if (root is not null && root.Kind != SchemaKind.Object)
        {
            _context.Add(
                ToolEnvelopePlanDiagnosticCode.NonObjectSchema,
                "Tool arguments must declare type object at the schema root.",
                "/type");
        }

        if (_nextNodeId > Limits.MaxSchemaRules)
        {
            _context.Add(
                ToolEnvelopePlanDiagnosticCode.TooManySchemaRules,
                $"The schema needs {_nextNodeId} rules; the plan limit is {Limits.MaxSchemaRules}.",
                string.Empty);
        }

        return root is null || _context.DiagnosticCount != before
            ? null
            : new CompiledTool(Tool, root, _nextNodeId, _maximumDepth);
    }

    private SchemaNode? CompileNode(JsonElement schema, string pointer, int depth)
    {
        if (_compiledByPointer.TryGetValue(pointer, out var existing))
        {
            var deepestUse = depth + existing.InstanceDepth - 1;
            if (deepestUse > Limits.MaxSchemaDepth)
            {
                _context.Add(
                    ToolEnvelopePlanDiagnosticCode.SchemaTooDeep,
                    $"The schema exceeds the maximum depth of {Limits.MaxSchemaDepth}.",
                    pointer);
                return null;
            }

            _maximumDepth = Math.Max(_maximumDepth, deepestUse);
            return existing;
        }

        if (depth > Limits.MaxSchemaDepth)
        {
            _context.Add(
                ToolEnvelopePlanDiagnosticCode.SchemaTooDeep,
                $"The schema exceeds the maximum depth of {Limits.MaxSchemaDepth}.",
                pointer);
            return null;
        }

        _maximumDepth = Math.Max(_maximumDepth, depth);

        if (schema.ValueKind != JsonValueKind.Object)
        {
            _context.Add(
                ToolEnvelopePlanDiagnosticCode.InvalidSchema,
                "Each schema node must be a JSON object.",
                pointer);
            return null;
        }

        if (!_keywords.CheckUniqueProperties(schema, pointer))
            return null;

        _keywords.ValidateDialect(schema, pointer);
        if (!_activePointers.Add(pointer))
        {
            _context.Add(
                ToolEnvelopePlanDiagnosticCode.CircularReference,
                "Recursive schemas are not supported.",
                pointer);
            return null;
        }

        try
        {
            CompileDefinitions(schema, pointer);
            if (schema.TryGetProperty("$ref", out var reference))
                return CompileReference(schema, reference, pointer, depth);

            if (!_keywords.TryReadKind(schema, pointer, out var kind))
                return null;

            _keywords.ValidateKeywords(schema, kind, pointer);
            var description = _keywords.ReadDescription(schema, pointer);
            var supportsLiterals = kind is not (SchemaKind.Object or SchemaKind.Array);
            var enumeration = supportsLiterals
                ? _keywords.ReadEnumeration(schema, kind, pointer)
                : Array.Empty<JsonElement>();
            var constant = supportsLiterals
                ? _keywords.ReadConstant(schema, kind, pointer)
                : null;
            if (supportsLiterals)
                _keywords.ValidateConstantAgainstEnumeration(constant, enumeration, pointer);

            var id = _nextNodeId++;
            SchemaNode? node = kind switch
            {
                SchemaKind.Object => CompileObject(
                    schema, pointer, depth, id, description),
                SchemaKind.Array => CompileArray(
                    schema, pointer, depth, id, description),
                SchemaKind.String => CompileString(
                    schema, pointer, id, description, enumeration, constant),
                SchemaKind.Integer or SchemaKind.Number => CompileNumber(
                    schema, pointer, id, kind, description, enumeration, constant),
                SchemaKind.Boolean or SchemaKind.Null => new SchemaNode
                {
                    Id = id,
                    Pointer = pointer,
                    Kind = kind,
                    InstanceDepth = 1,
                    Description = description,
                    EnumValues = enumeration,
                    Constant = constant,
                },
                _ => throw new InvalidOperationException(
                    $"Schema field '{pointer}' compiled to unsupported kind '{kind}'. This "
                    + "indicates a package defect because TryReadKind must admit only kinds handled "
                    + "by CompileNode. No plan was produced; report the schema to the package "
                    + "maintainer."),
            };

            if (node is not null)
                _compiledByPointer[pointer] = node;
            return node;
        }
        finally
        {
            _activePointers.Remove(pointer);
        }
    }

    private void CompileDefinitions(JsonElement schema, string pointer)
    {
        if (!schema.TryGetProperty("$defs", out var definitions))
            return;

        var definitionsPointer = JsonPointers.Property(pointer, "$defs");
        if (definitions.ValueKind != JsonValueKind.Object)
        {
            _context.Add(
                ToolEnvelopePlanDiagnosticCode.InvalidSchema,
                "The $defs field must be a JSON object whose properties are complete schemas.",
                definitionsPointer);
            return;
        }

        if (!_keywords.CheckUniqueProperties(definitions, definitionsPointer))
            return;

        foreach (var definition in definitions.EnumerateObject())
        {
            CompileNode(
                definition.Value,
                JsonPointers.Property(definitionsPointer, definition.Name),
                depth: 1);
        }
    }

    private SchemaNode? CompileReference(
        JsonElement schema,
        JsonElement reference,
        string pointer,
        int depth)
    {
        _keywords.ValidateReferenceKeywords(schema, pointer);
        var description = _keywords.ReadDescription(schema, pointer);

        if (reference.ValueKind != JsonValueKind.String
            || reference.GetString() is not { } referenceText
            || !referenceText.StartsWith('#'))
        {
            _context.Add(
                ToolEnvelopePlanDiagnosticCode.UnknownReference,
                "Only local JSON Pointer references beginning with # are supported.",
                JsonPointers.Property(pointer, "$ref"));
            return null;
        }

        if (!TryResolveReference(referenceText, out var target, out var targetPointer))
        {
            _context.Add(
                ToolEnvelopePlanDiagnosticCode.UnknownReference,
                $"The local reference '{referenceText}' does not resolve.",
                JsonPointers.Property(pointer, "$ref"));
            return null;
        }

        if (_activePointers.Contains(targetPointer))
        {
            _context.Add(
                ToolEnvelopePlanDiagnosticCode.CircularReference,
                $"The local reference '{referenceText}' is recursive.",
                JsonPointers.Property(pointer, "$ref"));
            return null;
        }

        if (_compiledByPointer.TryGetValue(targetPointer, out var compiled)
            && depth + compiled.InstanceDepth - 1 > Limits.MaxSchemaDepth)
        {
            _context.Add(
                ToolEnvelopePlanDiagnosticCode.SchemaTooDeep,
                $"The referenced schema exceeds the maximum depth of {Limits.MaxSchemaDepth} at this use.",
                JsonPointers.Property(pointer, "$ref"));
            return null;
        }

        var resolved = CompileNode(target, targetPointer, depth);
        return resolved is null || description is null
            ? resolved
            : resolved with { Description = description };
    }

    private SchemaNode? CompileObject(
        JsonElement schema,
        string pointer,
        int depth,
        int id,
        string? description)
    {
        if (!schema.TryGetProperty("additionalProperties", out var additional)
            || additional.ValueKind != JsonValueKind.False)
        {
            _context.Add(
                ToolEnvelopePlanDiagnosticCode.OpenObject,
                "Every object schema must set additionalProperties to false.",
                JsonPointers.Property(pointer, "additionalProperties"));
        }

        var requiredNames = _keywords.ReadRequiredNames(schema, pointer);
        var properties = new List<SchemaProperty>();

        if (schema.TryGetProperty("properties", out var propertySchemas))
        {
            if (propertySchemas.ValueKind != JsonValueKind.Object)
            {
                _context.Add(
                    ToolEnvelopePlanDiagnosticCode.InvalidSchema,
                    "Object properties must be a JSON object.",
                    JsonPointers.Property(pointer, "properties"));
            }
            else
            {
                _keywords.CheckUniqueProperties(
                    propertySchemas,
                    JsonPointers.Property(pointer, "properties"));
                var entries = propertySchemas.EnumerateObject().ToArray();
                if (entries.Length > Limits.MaxPropertiesPerObject)
                {
                    _context.Add(
                        ToolEnvelopePlanDiagnosticCode.TooManyProperties,
                        $"The object has {entries.Length} properties; the plan limit is "
                        + $"{Limits.MaxPropertiesPerObject}.",
                        JsonPointers.Property(pointer, "properties"));
                }

                var knownNames = new HashSet<string>(entries.Select(entry => entry.Name), StringComparer.Ordinal);
                foreach (var requiredName in requiredNames)
                {
                    if (!knownNames.Contains(requiredName))
                    {
                        _context.Add(
                            ToolEnvelopePlanDiagnosticCode.InvalidRequiredProperty,
                            $"Required property '{requiredName}' is not declared in properties.",
                            JsonPointers.Property(pointer, "required"));
                    }
                }

                foreach (var entry in entries)
                {
                    var propertyPointer = JsonPointers.Property(
                        JsonPointers.Property(pointer, "properties"),
                        entry.Name);
                    var child = CompileNode(entry.Value, propertyPointer, depth + 1);
                    if (child is not null)
                    {
                        properties.Add(new SchemaProperty(
                            entry.Name,
                            child,
                            requiredNames.Contains(entry.Name),
                            propertyPointer));
                    }
                }
            }
        }
        else if (requiredNames.Count > 0)
        {
            _context.Add(
                ToolEnvelopePlanDiagnosticCode.InvalidRequiredProperty,
                "A schema with required names must declare properties.",
                JsonPointers.Property(pointer, "required"));
        }

        return new SchemaNode
        {
            Id = id,
            Pointer = pointer,
            Kind = SchemaKind.Object,
            InstanceDepth = properties.Count == 0
                ? 1
                : 1 + properties.Max(property => property.Schema.InstanceDepth),
            Description = description,
            Properties = properties.AsReadOnly(),
        };
    }

    private SchemaNode? CompileArray(
        JsonElement schema,
        string pointer,
        int depth,
        int id,
        string? description)
    {
        if (!schema.TryGetProperty("items", out var items))
        {
            _context.Add(
                ToolEnvelopePlanDiagnosticCode.InvalidSchema,
                "An array schema must declare one item schema.",
                JsonPointers.Property(pointer, "items"));
            return null;
        }

        var minimum = _keywords.ReadNonNegativeInteger(schema, "minItems", pointer, 0);
        var declaredMaximum = _keywords.ReadNonNegativeInteger(
            schema,
            "maxItems",
            pointer,
            Limits.MaxGeneratedArrayItems);
        var maximum = Math.Min(declaredMaximum, Limits.MaxGeneratedArrayItems);
        if (minimum > maximum)
        {
            _context.Add(
                ToolEnvelopePlanDiagnosticCode.InvalidConstraint,
                $"Array minItems {minimum} exceeds its effective maximum {maximum}.",
                pointer);
        }

        var itemNode = CompileNode(items, JsonPointers.Property(pointer, "items"), depth + 1);
        return itemNode is null
            ? null
            : new SchemaNode
            {
                Id = id,
                Pointer = pointer,
                Kind = SchemaKind.Array,
                InstanceDepth = 1 + itemNode.InstanceDepth,
                Description = description,
                Items = itemNode,
                MinimumItems = minimum,
                MaximumItems = maximum,
            };
    }

    private SchemaNode CompileString(
        JsonElement schema,
        string pointer,
        int id,
        string? description,
        IReadOnlyList<JsonElement> enumeration,
        JsonElement? constant)
    {
        var minimum = _keywords.ReadNonNegativeInteger(schema, "minLength", pointer, 0);
        var declaredMaximum = _keywords.ReadNonNegativeInteger(
            schema,
            "maxLength",
            pointer,
            Limits.MaxGeneratedStringCharacters);
        var maximum = Math.Min(declaredMaximum, Limits.MaxGeneratedStringCharacters);
        if (minimum > maximum)
        {
            _context.Add(
                ToolEnvelopePlanDiagnosticCode.InvalidConstraint,
                $"String minLength {minimum} exceeds its effective maximum {maximum}.",
                pointer);
        }

        var effectiveEnumeration = enumeration
            .Where((value, index) => ValidateStringLiteral(
                value,
                JsonPointers.Index(JsonPointers.Property(pointer, "enum"), index),
                "enum"))
            .Where(value =>
            {
                var length = value.GetString()!.EnumerateRunes().Count();
                return length >= minimum && length <= maximum;
            })
            .ToArray();
        if (enumeration.Count > 0 && effectiveEnumeration.Length == 0)
        {
            _context.Add(
                ToolEnvelopePlanDiagnosticCode.InvalidConstraint,
                "No enum value satisfies the effective string length constraints.",
                pointer);
        }

        if (constant is { ValueKind: JsonValueKind.String } stringConstant
            && ValidateStringLiteral(
                stringConstant,
                JsonPointers.Property(pointer, "const"),
                "const"))
        {
            var length = stringConstant.GetString()!.EnumerateRunes().Count();
            if (length < minimum || length > maximum)
            {
                _context.Add(
                    ToolEnvelopePlanDiagnosticCode.InvalidConstraint,
                    "The const value does not satisfy the effective string length constraints.",
                    JsonPointers.Property(pointer, "const"));
            }
        }

        return new SchemaNode
        {
            Id = id,
            Pointer = pointer,
            Kind = SchemaKind.String,
            InstanceDepth = 1,
            Description = description,
            MinimumLength = minimum,
            MaximumLength = maximum,
            EnumValues = Array.AsReadOnly(effectiveEnumeration),
            Constant = constant,
        };
    }

    private SchemaNode CompileNumber(
        JsonElement schema,
        string pointer,
        int id,
        SchemaKind kind,
        string? description,
        IReadOnlyList<JsonElement> enumeration,
        JsonElement? constant)
    {
        var minimum = _keywords.ReadDecimal(schema, "minimum", pointer);
        var maximum = _keywords.ReadDecimal(schema, "maximum", pointer);
        if (minimum is not null && maximum is not null && minimum > maximum)
        {
            _context.Add(
                ToolEnvelopePlanDiagnosticCode.InvalidConstraint,
                $"Numeric minimum {minimum} exceeds maximum {maximum}.",
                pointer);
        }

        var effectiveEnumeration = new List<JsonElement>();
        foreach (var value in enumeration)
        {
            if (!SchemaKeywordReader.TryReadNumber(value, out var number)
                || minimum is not null && number < minimum
                || maximum is not null && number > maximum)
            {
                continue;
            }

            var normalized = NormalizeNumber(number);
            if (normalized.GetRawText().Length <= Limits.MaxGeneratedNumberCharacters)
                effectiveEnumeration.Add(normalized);
        }

        if (enumeration.Count > 0 && effectiveEnumeration.Count == 0)
        {
            _context.Add(
                ToolEnvelopePlanDiagnosticCode.InvalidConstraint,
                "No enum value satisfies the numeric bounds and "
                + $"MaxGeneratedNumberCharacters limit of {Limits.MaxGeneratedNumberCharacters}.",
                pointer);
        }

        JsonElement? effectiveConstant = constant;
        if (constant is { } numericConstant
            && SchemaKeywordReader.TryReadNumber(numericConstant, out var constantNumber))
        {
            effectiveConstant = NormalizeNumber(constantNumber);
            if (minimum is not null && constantNumber < minimum
                || maximum is not null && constantNumber > maximum)
            {
                _context.Add(
                    ToolEnvelopePlanDiagnosticCode.InvalidConstraint,
                    "The const value does not satisfy the numeric bounds.",
                    JsonPointers.Property(pointer, "const"));
            }

            var literalCharacters = effectiveConstant.Value.GetRawText().Length;
            if (literalCharacters > Limits.MaxGeneratedNumberCharacters)
            {
                _context.Add(
                    ToolEnvelopePlanDiagnosticCode.InvalidConstraint,
                    $"The shortest exact const representation uses {literalCharacters} "
                    + "characters, but ToolEnvelopeLimits.MaxGeneratedNumberCharacters is "
                    + $"{Limits.MaxGeneratedNumberCharacters}.",
                    JsonPointers.Property(pointer, "const"));
            }
        }

        return new SchemaNode
        {
            Id = id,
            Pointer = pointer,
            Kind = kind,
            InstanceDepth = 1,
            Description = description,
            Minimum = minimum,
            Maximum = maximum,
            EnumValues = effectiveEnumeration.AsReadOnly(),
            Constant = effectiveConstant,
        };
    }

    private bool ValidateStringLiteral(
        JsonElement value,
        string literalPointer,
        string constraintName)
    {
        if (value.ValueKind != JsonValueKind.String)
            return false;
        if (JsonStringUnicode.TryValidate(value, out var unicodeProblem))
            return true;

        _context.Add(
            ToolEnvelopePlanDiagnosticCode.InvalidConstraint,
            $"The {constraintName} string is not a valid Unicode scalar sequence. "
            + unicodeProblem,
            literalPointer);
        return false;
    }

    private static JsonElement NormalizeNumber(decimal number)
    {
        var fixedOrGeneral = number.ToString("G29", CultureInfo.InvariantCulture);
        var scientific = number
            .ToString("0.############################E+0", CultureInfo.InvariantCulture)
            .Replace("E+", "E", StringComparison.Ordinal);
        var shortest = scientific.Length < fixedOrGeneral.Length
            ? scientific
            : fixedOrGeneral;
        using var document = JsonDocument.Parse(shortest);
        return document.RootElement.Clone();
    }

    private bool TryResolveReference(
        string reference,
        out JsonElement target,
        out string pointer)
    {
        target = Tool.Parameters;
        pointer = string.Empty;
        if (reference == "#")
            return true;

        if (!reference.StartsWith("#/", StringComparison.Ordinal))
            return false;

        foreach (var encodedSegment in reference[2..].Split('/'))
        {
            if (!JsonPointers.TryUnescape(encodedSegment, out var segment))
                return false;
            if (target.ValueKind == JsonValueKind.Object)
            {
                if (!target.TryGetProperty(segment, out target))
                    return false;
            }
            else if (target.ValueKind == JsonValueKind.Array
                      && int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                      && (segment == "0" || segment[0] != '0')
                      && index >= 0
                     && index < target.GetArrayLength())
            {
                target = target[index];
            }
            else
            {
                return false;
            }

            pointer = JsonPointers.Property(pointer, segment);
        }

        return true;
    }
}

using System.Text;

namespace LlamaSharp.ToolCallEnvelopes;

/// <summary>
/// Builds complete GBNF alternatives for LlamaSharp tool envelopes.
/// </summary>
public static class LlamaSharpToolGrammar
{
    private static readonly string DefaultGrammar =
        BuildDeclaredGrammar(
            ToolChoice.Auto,
            parallelCalls: true,
            [],
            strict: false,
            allowRefusal: false,
            catalogAuthoritative: false);

    /// <summary>
    /// Returns the original explicit envelope grammar for an auto turn.
    /// </summary>
    public static string Build() => DefaultGrammar;

    /// <summary>
    /// Builds a complete explicit envelope grammar using the original package
    /// contract.
    /// </summary>
    public static string Build(
        ToolChoice choice,
        bool parallelCalls = true,
        bool allowRefusal = false)
    {
        ArgumentNullException.ThrowIfNull(choice);
        return BuildDeclaredGrammar(
            choice,
            parallelCalls,
            [],
            strict: false,
            allowRefusal,
            catalogAuthoritative: false);
    }

    /// <summary>
    /// Builds a complete explicit envelope grammar with optional per-tool
    /// schema enforcement.
    /// </summary>
    public static string Build(
        ToolChoice choice,
        bool parallelCalls,
        IReadOnlyList<ToolDefinition> tools,
        bool strict,
        bool allowRefusal = false)
    {
        ArgumentNullException.ThrowIfNull(choice);
        ArgumentNullException.ThrowIfNull(tools);
        ValidateAuthoritativeCatalog(choice, tools);
        return BuildDeclaredGrammar(
            choice,
            parallelCalls,
            tools,
            strict,
            allowRefusal,
            catalogAuthoritative: true);
    }

    /// <summary>
    /// Builds the complete-envelope grammar described by <paramref name="options"/>.
    /// The root is a union of whole message, tool-call, and refusal alternatives;
    /// mode and payload fields are never generated independently.
    /// </summary>
    public static string BuildCompleteEnvelopeGrammar(
        IReadOnlyList<ToolDefinition> tools,
        ToolEnvelopeGrammarOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        options ??= new ToolEnvelopeGrammarOptions();
        ArgumentNullException.ThrowIfNull(options.ToolChoice);
        ValidateAuthoritativeCatalog(options.ToolChoice, tools);

        return options.EnvelopeMode switch
        {
            ToolEnvelopeMode.Inferred => BuildInferredGrammar(
                options.ToolChoice,
                options.ParallelToolCalls,
                tools,
                options.StrictTools,
                options.AllowRefusal,
                catalogAuthoritative: true),
            ToolEnvelopeMode.StrictDeclared => BuildDeclaredGrammar(
                options.ToolChoice,
                options.ParallelToolCalls,
                tools,
                options.StrictTools,
                options.AllowRefusal,
                catalogAuthoritative: true),
            _ => throw new ArgumentOutOfRangeException(
                nameof(options), options.EnvelopeMode, "Unknown envelope mode.")
        };
    }

    /// <summary>
    /// Builds a complete-envelope grammar using an options object. This
    /// overload is convenient when the tool list is assembled dynamically.
    /// </summary>
    public static string Build(
        IReadOnlyList<ToolDefinition> tools,
        ToolEnvelopeGrammarOptions? options = null) =>
        BuildCompleteEnvelopeGrammar(tools, options);

    private static string BuildDeclaredGrammar(
        ToolChoice choice,
        bool parallelCalls,
        IReadOnlyList<ToolDefinition> tools,
        bool strict,
        bool allowRefusal,
        bool catalogAuthoritative)
    {
        var rules = BuildCallRules(choice, tools, strict, argumentProperty: "args");
        var allowMessage = choice.Mode is ToolChoiceMode.Auto or ToolChoiceMode.None;
        var allowToolCalls = (choice.Mode is ToolChoiceMode.Auto or ToolChoiceMode.Required or ToolChoiceMode.Named)
                             && (!catalogAuthoritative || tools.Count > 0);
        var allowRefusalBranch = allowRefusal && allowMessage;

        var callArray = BuildCallsArray(choice, parallelCalls, rules.CallObjectRule);
        var sb = new StringBuilder();
        var rootAlternatives = new List<string>();

        if (allowMessage)
        {
            rootAlternatives.Add("message-envelope");
            sb.AppendLine(
                "message-envelope ::= \"{\" ws \"\\\"mode\\\"\" ws \":\" ws \"\\\"message\\\"\" ws \",\" ws \"\\\"text\\\"\" ws \":\" ws string ws \",\" ws \"\\\"calls\\\"\" ws \":\" ws empty-calls \"}\"");
        }

        if (allowToolCalls)
        {
            rootAlternatives.Add("tool-calls-envelope");
            sb.AppendLine(
                "tool-calls-envelope ::= \"{\" ws \"\\\"mode\\\"\" ws \":\" ws \"\\\"tool_calls\\\"\" ws \",\" ws \"\\\"text\\\"\" ws \":\" ws string ws \",\" ws \"\\\"calls\\\"\" ws \":\" ws calls-arr \"}\"");
        }

        if (allowRefusalBranch)
        {
            rootAlternatives.Add("refusal-envelope");
            sb.AppendLine(
                "refusal-envelope ::= \"{\" ws \"\\\"mode\\\"\" ws \":\" ws \"\\\"refusal\\\"\" ws \",\" ws \"\\\"text\\\"\" ws \":\" ws string ws \",\" ws \"\\\"calls\\\"\" ws \":\" ws empty-calls \"}\"");
        }

        if (rootAlternatives.Count == 0)
            throw new InvalidOperationException("ToolChoice produced an unreachable grammar.");

        sb.Insert(0, $"root ::= {string.Join(" | ", rootAlternatives)}\n\n");
        sb.AppendLine("empty-calls ::= \"[\" ws \"]\"");
        sb.AppendLine($"calls-arr ::= {callArray}");
        var modeAlternatives = new List<string>();
        if (allowMessage) modeAlternatives.Add("\"\\\"message\\\"\"");
        if (allowToolCalls) modeAlternatives.Add("\"\\\"tool_calls\\\"\"");
        if (allowRefusalBranch) modeAlternatives.Add("\"\\\"refusal\\\"\"");
        sb.AppendLine($"mode-val ::= {string.Join(" | ", modeAlternatives)}");
        AppendCallRules(sb, rules);
        AppendJsonPrimitives(sb);
        return sb.ToString();
    }

    private static string BuildInferredGrammar(
        ToolChoice choice,
        bool parallelCalls,
        IReadOnlyList<ToolDefinition> tools,
        bool strict,
        bool allowRefusal,
        bool catalogAuthoritative)
    {
        var rules = BuildCallRules(choice, tools, strict, argumentProperty: "arguments");
        var allowMessage = choice.Mode is ToolChoiceMode.Auto or ToolChoiceMode.None;
        var allowToolCalls = (choice.Mode is ToolChoiceMode.Auto or ToolChoiceMode.Required or ToolChoiceMode.Named)
                             && (!catalogAuthoritative || tools.Count > 0);
        var allowRefusalBranch = allowRefusal && allowMessage;
        var rootAlternatives = new List<string>();
        var sb = new StringBuilder();

        if (allowMessage)
        {
            rootAlternatives.Add("inferred-message-envelope");
            sb.AppendLine("inferred-message-envelope ::= \"{\" ws \"\\\"text\\\"\" ws \":\" ws string \"}\"");
        }

        if (allowToolCalls)
        {
            rootAlternatives.Add("inferred-tool-calls-envelope");
            sb.AppendLine(
                "inferred-tool-calls-envelope ::= \"{\" ws \"\\\"tool_calls\\\"\" ws \":\" ws calls-arr \"}\"");
        }

        if (allowRefusalBranch)
        {
            rootAlternatives.Add("inferred-refusal-envelope");
            sb.AppendLine("inferred-refusal-envelope ::= \"{\" ws \"\\\"refusal\\\"\" ws \":\" ws string \"}\"");
        }

        if (rootAlternatives.Count == 0)
            throw new InvalidOperationException("ToolChoice produced an unreachable grammar.");

        sb.Insert(0, $"root ::= {string.Join(" | ", rootAlternatives)}\n\n");
        sb.AppendLine($"calls-arr ::= {BuildCallsArray(choice, parallelCalls, rules.CallObjectRule)}");
        AppendCallRules(sb, rules);
        AppendJsonPrimitives(sb);
        return sb.ToString();
    }

    private static CallGrammarRules BuildCallRules(
        ToolChoice choice,
        IReadOnlyList<ToolDefinition> tools,
        bool strict,
        string argumentProperty)
    {
        if (choice.Mode == ToolChoiceMode.None)
            return new CallGrammarRules("call-obj", string.Empty);

        var fragments = new List<(string ToolName, string ArgsRule, string SchemaBody)>();

        for (var index = 0; index < tools.Count; index++)
        {
            var tool = tools[index];
            if (choice.Mode == ToolChoiceMode.Named
                && !string.Equals(tool.Name, choice.NamedFunction, StringComparison.Ordinal))
            {
                continue;
            }

            if (strict)
            {
                IReadOnlyList<string> unsupported = ["/ (non-object schema root)"];
                if (tool.ParametersSchema.ValueKind != System.Text.Json.JsonValueKind.Object)
                {
                    throw new LlamaSharpToolSchemaException(tool.Name, unsupported);
                }

                var parameterSchema = NormalizeToolParameterSchema(tool);
                if (!LlamaSharpJsonSchemaConverter.TryConvertFragment(
                        parameterSchema,
                        $"t{index}-",
                        out var topRule,
                        out var body,
                        out unsupported)
                    || unsupported.Count > 0)
                {
                    throw new LlamaSharpToolSchemaException(tool.Name, unsupported);
                }

                fragments.Add((tool.Name, topRule, body));
            }
            else
            {
                fragments.Add((tool.Name, "obj", string.Empty));
            }
        }

        if (choice.Mode == ToolChoiceMode.Named && strict && fragments.Count == 0)
        {
            throw new LlamaSharpToolSchemaException(
                choice.NamedFunction ?? "<none>",
                ["Named tool choice did not match any supplied tool."]);
        }

        var callObjectRule = fragments.Count == 0
            ? "call-obj"
            : fragments.Count == 1
                ? "call-obj-0"
                : "call-obj-union";

        var sb = new StringBuilder();
        if (fragments.Count == 0)
        {
            sb.AppendLine(
                $"call-obj ::= \"{{\" ws \"\\\"id\\\"\" ws \":\" ws string ws \",\" ws \"\\\"name\\\"\" ws \":\" ws {BuildNameTerminal(choice)} ws \",\" ws \"\\\"{argumentProperty}\\\"\" ws \":\" ws obj \"}}\"");
        }
        else
        {
            if (fragments.Count > 1)
                sb.AppendLine($"call-obj-union ::= {string.Join(" | ", fragments.Select((_, i) => $"call-obj-{i}"))}");

            for (var i = 0; i < fragments.Count; i++)
            {
                var (name, argsRule, _) = fragments[i];
                var nameTerminal = choice.Mode == ToolChoiceMode.Named
                    ? BuildNameTerminal(choice)
                    : BuildJsonStringTerminal(name);
                sb.AppendLine(
                    $"call-obj-{i} ::= \"{{\" ws \"\\\"id\\\"\" ws \":\" ws string ws \",\" ws \"\\\"name\\\"\" ws \":\" ws {nameTerminal} ws \",\" ws \"\\\"{argumentProperty}\\\"\" ws \":\" ws {argsRule} \"}}\"");
            }
        }

        foreach (var (_, _, body) in fragments)
        {
            if (!string.IsNullOrWhiteSpace(body))
            {
                sb.AppendLine(body.TrimEnd());
            }
        }

        return new CallGrammarRules(callObjectRule, sb.ToString());
    }

    private static System.Text.Json.JsonElement NormalizeToolParameterSchema(
        ToolDefinition tool)
    {
        var schema = tool.ParametersSchema;
        if (schema.TryGetProperty("type", out var type))
        {
            if (type.ValueKind == System.Text.Json.JsonValueKind.String
                && string.Equals(type.GetString(), "object", StringComparison.Ordinal))
            {
                return schema;
            }

            throw new LlamaSharpToolSchemaException(
                tool.Name,
                ["/type (tool arguments must have an object root)"]);
        }

        using var stream = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            foreach (var property in schema.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                property.Value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        using var document = System.Text.Json.JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static void AppendCallRules(StringBuilder sb, CallGrammarRules rules)
    {
        if (rules.Body.Length > 0)
            sb.Append(rules.Body);
    }

    private static string BuildCallsArray(
        ToolChoice choice,
        bool parallelCalls,
        string callObjectRule)
    {
        var single = !parallelCalls || choice.Mode == ToolChoiceMode.Named;
        return choice.Mode switch
        {
            ToolChoiceMode.None => "\"[\" ws \"]\"",
            ToolChoiceMode.Auto when parallelCalls =>
                $"\"[\" ws {callObjectRule} ( ws \",\" ws {callObjectRule} )* ws \"]\"",
            ToolChoiceMode.Auto => $"\"[\" ws {callObjectRule} ws \"]\"",
            _ when single => $"\"[\" ws {callObjectRule} ws \"]\"",
            _ => $"\"[\" ws {callObjectRule} ( ws \",\" ws {callObjectRule} )* ws \"]\"",
        };
    }

    private static string BuildNameTerminal(ToolChoice choice) =>
        choice.Mode == ToolChoiceMode.Named
            ? $"\"\\\"{EscapeForJsonInsideGbnf(choice.NamedFunction!)}\\\"\""
            : "string";

    private static string BuildJsonStringTerminal(string value)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(value);
        var terminal = new StringBuilder(json.Length + 2);
        terminal.Append('"');
        foreach (var character in json)
        {
            if (character is '"' or '\\')
                terminal.Append('\\');
            terminal.Append(character);
        }
        terminal.Append('"');
        return terminal.ToString();
    }

    private static void ValidateAuthoritativeCatalog(
        ToolChoice choice,
        IReadOnlyList<ToolDefinition> tools)
    {
        if (choice.Mode == ToolChoiceMode.Required && tools.Count == 0)
        {
            throw new ArgumentException(
                "ToolChoice.Required requires at least one supplied tool.",
                nameof(tools));
        }

        if (choice.Mode == ToolChoiceMode.Named
            && !tools.Any(tool => string.Equals(
                tool.Name,
                choice.NamedFunction,
                StringComparison.Ordinal)))
        {
            throw new LlamaSharpToolSchemaException(
                choice.NamedFunction ?? "<none>",
                ["Named tool choice did not match any supplied tool."]);
        }
    }

    private static void AppendJsonPrimitives(StringBuilder sb)
    {
        sb.AppendLine("obj ::= \"{\" ws \"}\" | \"{\" ws kv-pair ( ws \",\" ws kv-pair )* ws \"}\"");
        sb.AppendLine("kv-pair ::= string ws \":\" ws value");
        sb.AppendLine("value ::= string | number | obj | arr | \"true\" | \"false\" | \"null\"");
        sb.AppendLine("arr ::= \"[\" ws \"]\" | \"[\" ws value ( ws \",\" ws value )* ws \"]\"");
        sb.AppendLine("string ::= \"\\\"\" char* \"\\\"\"");
        sb.AppendLine("char ::= [^\"\\\\\\x00-\\x1F] | \"\\\\\" ( \"\\\"\" | \"\\\\\" | \"/\" | \"b\" | \"f\" | \"n\" | \"r\" | \"t\" ) | \"\\\\\" \"u\" [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F]");
        sb.AppendLine("number ::= \"-\"? ( \"0\" | [1-9] [0-9]* ) ( \".\" [0-9]+ )? ( [eE] [+-]? [0-9]+ )?");
        sb.AppendLine("integer ::= \"-\"? ( \"0\" | [1-9] [0-9]* )");
        sb.AppendLine("boolean ::= \"true\" | \"false\"");
        sb.AppendLine("null-lit ::= \"null\"");
        sb.AppendLine("object ::= obj");
        sb.AppendLine("object-kv ::= kv-pair");
        sb.AppendLine("array ::= arr");
        sb.AppendLine("ws ::= [ \\t\\n\\r]*");
    }

    private static string EscapeForJsonInsideGbnf(string name)
    {
        foreach (var ch in name)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.'))
            {
                throw new ArgumentException(
                    $"Invalid character '{ch}' in function name '{name}' - named tool choice requires [A-Za-z0-9_.-].",
                    nameof(name));
            }
        }

        return name;
    }

    private sealed record CallGrammarRules(string CallObjectRule, string Body);
}

/// <summary>
/// Options for the complete-envelope grammar builder.
/// </summary>
public sealed record ToolEnvelopeGrammarOptions
{
    public ToolChoice ToolChoice { get; init; } = ToolChoice.Auto;
    public ToolEnvelopeMode EnvelopeMode { get; init; } = ToolEnvelopeMode.Inferred;
    public bool ParallelToolCalls { get; init; } = true;
    public bool StrictTools { get; init; } = true;
    public bool AllowRefusal { get; init; }
}

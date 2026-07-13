using System.Text;
using System.Text.Json;

namespace LlamaSharp.ToolCallEnvelopes.Internal;

internal static class GrammarBuilder
{
    internal static string Build(
        ToolChoice choice,
        IReadOnlyList<CompiledTool> tools,
        ToolEnvelopePlanOptions options)
    {
        var builder = new Builder(choice, tools, options);
        return builder.Build();
    }

    private sealed class Builder
    {
        private readonly ToolChoice _choice;
        private readonly IReadOnlyList<CompiledTool> _tools;
        private readonly ToolEnvelopePlanOptions _options;
        private readonly List<(string Name, string Body)> _rules = [];
        private readonly HashSet<string> _ruleNames = new(StringComparer.Ordinal);
        private readonly Dictionary<(int ToolIndex, int NodeId), string> _schemaRules = [];
        private readonly Dictionary<(string Atom, int Maximum), string> _atMostRules = [];
        private readonly Dictionary<(string Atom, int Power), string> _exactPowerRules = [];
        private bool _usesJsonCharacter;
        private bool _usesInteger;
        private bool _usesNumber;

        internal Builder(
            ToolChoice choice,
            IReadOnlyList<CompiledTool> tools,
            ToolEnvelopePlanOptions options)
        {
            _choice = choice;
            _tools = tools;
            _options = options;
        }

        internal string Build()
        {
            var rootAlternatives = new List<string>();
            var allowsAssistant = _choice.Kind is ToolChoiceKind.Auto or ToolChoiceKind.None;
            var selectedTools = SelectTools();

            if (allowsAssistant)
            {
                rootAlternatives.Add("assistant-message");
                _usesJsonCharacter = true;
                AddRule(
                    "assistant-message",
                    $"{Terminal("{")} gap {Terminal("\"text\"")} gap {Terminal(":")} gap "
                    + $"assistant-text gap {Terminal("}")}");
                AddRule(
                    "assistant-text",
                    QuotedString(
                        1,
                        _options.Limits.MaxFinalTextCharacters,
                        "assistant-character"));
            }

            if (selectedTools.Count > 0
                && (_choice.Kind is ToolChoiceKind.Auto
                    or ToolChoiceKind.Required
                    or ToolChoiceKind.Named))
            {
                rootAlternatives.Add("tool-request");
                AddToolRequest(selectedTools);
            }

            if (allowsAssistant && _options.AllowRefusal)
            {
                rootAlternatives.Add("refusal");
                _usesJsonCharacter = true;
                AddRule(
                    "refusal",
                    $"{Terminal("{")} gap {Terminal("\"refusal\"")} gap {Terminal(":")} gap "
                    + $"refusal-text gap {Terminal("}")}");
                AddRule(
                    "refusal-text",
                    QuotedString(
                        1,
                        _options.Limits.MaxRefusalCharacters,
                        "refusal-character"));
            }

            if (rootAlternatives.Count == 0)
            {
                throw new InvalidOperationException(
                    $"The compiled turn has no legal output branch for choice '{_choice}', "
                    + $"{_tools.Count} catalog tool(s), and AllowRefusal={_options.AllowRefusal}. "
                    + "This indicates a package defect because plan validation must reject an "
                    + "impossible policy before grammar construction. Do not start inference; "
                    + "report the plan options and catalog to the package maintainer.");
            }

            AddSharedRules();
            var output = new StringBuilder();
            output.Append("root ::= ").AppendJoin(" | ", rootAlternatives).AppendLine();
            foreach (var (name, body) in _rules)
                output.Append(name).Append(" ::= ").Append(body).AppendLine();

            if (_usesJsonCharacter)
            {
                output.AppendLine(
                    "json-char ::= [^\"\\\\\\x00-\\x1F] "
                    + "| \"\\\\\" ( \"\\\"\" | \"\\\\\" | \"/\" | \"b\" | \"f\" | \"n\" | \"r\" | \"t\" ) "
                    + "| unicode-bmp | unicode-pair");
                output.AppendLine(
                    "unicode-bmp ::= \"\\\\\" \"u\" "
                    + "( [0-9a-cA-C] hex hex hex | [dD] [0-7] hex hex "
                    + "| [e-fE-F] hex hex hex )");
                output.AppendLine(
                    "unicode-pair ::= \"\\\\\" \"u\" [dD] [89aAbB] hex hex "
                    + "\"\\\\\" \"u\" [dD] [c-fC-F] hex hex");
                output.AppendLine("hex ::= [0-9a-fA-F]");
            }

            output.AppendLine("gap ::= [ \\t]?");
            return output.ToString().ReplaceLineEndings("\n");
        }

        private void AddSharedRules()
        {
            if (_usesInteger || _usesNumber)
            {
                var maximum = _options.Limits.MaxGeneratedNumberCharacters;
                AddRule(
                    "integer-value",
                    UnsignedInteger(maximum, "positive-integer-digit")
                    + " | "
                    + Terminal("-")
                    + " ( "
                    + UnsignedInteger(maximum - 1, "negative-integer-digit")
                    + " )");
            }

            if (_usesNumber)
            {
                AddRule(
                    "number-value",
                    "integer-value | "
                    + Decimal(
                        negative: false,
                        _options.Limits.MaxGeneratedNumberCharacters,
                        "positive-decimal")
                    + " | "
                    + Decimal(
                        negative: true,
                        _options.Limits.MaxGeneratedNumberCharacters,
                        "negative-decimal"));
            }
        }

        private IReadOnlyList<(int Index, CompiledTool Tool)> SelectTools()
        {
            if (_choice.Kind == ToolChoiceKind.None)
                return [];

            if (_choice.Kind == ToolChoiceKind.Named)
            {
                return _tools
                    .Select((tool, index) => (Index: index, Tool: tool))
                    .Where(item => string.Equals(
                        item.Tool.Definition.Name,
                        _choice.ToolName,
                        StringComparison.Ordinal))
                    .ToArray();
            }

            return _tools.Select((tool, index) => (Index: index, Tool: tool)).ToArray();
        }

        private void AddToolRequest(IReadOnlyList<(int Index, CompiledTool Tool)> tools)
        {
            var callRules = new List<string>(tools.Count);
            foreach (var (toolIndex, tool) in tools)
            {
                var argumentsRule = EnsureSchemaRule(toolIndex, tool.Arguments);
                var callRule = $"tool-call-{toolIndex}";
                callRules.Add(callRule);
                AddRule(
                    callRule,
                    $"{Terminal("{")} gap {Terminal("\"name\"")} gap {Terminal(":")} gap "
                    + $"{Terminal(JsonSerializer.Serialize(tool.Definition.Name))} gap "
                    + $"{Terminal(",")} gap {Terminal("\"arguments\"")} gap {Terminal(":")} gap "
                    + $"{argumentsRule} gap {Terminal("}")}");
            }

            AddRule("tool-call", string.Join(" | ", callRules));

            var repeat = BoundedRepetition(
                $"( gap {Terminal(",")} gap tool-call )",
                0,
                _options.MaxCallsPerTurn - 1,
                "additional-tool-call");
            AddRule(
                "tool-request",
                $"{Terminal("{")} gap {Terminal("\"tool_calls\"")} gap {Terminal(":")} gap "
                + $"{Terminal("[")} gap tool-call"
                + (repeat.Length == 0 ? string.Empty : $" {repeat}")
                + $" gap {Terminal("]")} gap {Terminal("}")}");
        }

        private string EnsureSchemaRule(int toolIndex, SchemaNode schema)
        {
            var key = (toolIndex, schema.Id);
            if (_schemaRules.TryGetValue(key, out var existing))
                return existing;

            var name = $"t{toolIndex}-schema-{schema.Id}";
            _schemaRules[key] = name;

            var body = schema.Constant is { } constant
                ? Terminal(constant.GetRawText())
                : schema.EnumValues.Count > 0
                    ? string.Join(
                        " | ",
                        schema.EnumValues
                            .Select(value => value.GetRawText())
                            .Distinct(StringComparer.Ordinal)
                            .Select(Terminal))
                    : schema.Kind switch
                    {
                        SchemaKind.Object => BuildObject(toolIndex, schema),
                        SchemaKind.Array => BuildArray(toolIndex, schema),
                        SchemaKind.String => BuildString(toolIndex, schema),
                        SchemaKind.Integer => UseInteger(),
                        SchemaKind.Number => UseNumber(),
                        SchemaKind.Boolean => $"{Terminal("true")} | {Terminal("false")}",
                        SchemaKind.Null => Terminal("null"),
                        _ => throw new InvalidOperationException(
                            $"Compiled schema node {schema.Id} at '{schema.Pointer}' has unsupported "
                            + $"kind '{schema.Kind}'. This indicates a package defect because every "
                            + "admitted schema kind must have an explicit grammar rule. Do not start "
                            + "inference; report the schema and plan diagnostics to the package "
                            + "maintainer."),
                    };

            AddRule(name, body);
            return name;
        }

        private string BuildObject(int toolIndex, SchemaNode schema)
        {
            var required = schema.Properties.Where(property => property.IsRequired).ToArray();
            var optional = schema.Properties.Where(property => !property.IsRequired).ToArray();

            if (required.Length == 0 && optional.Length == 0)
                return $"{Terminal("{")} gap {Terminal("}")}";

            if (required.Length > 0)
            {
                var body = new StringBuilder();
                body.Append(Terminal("{")).Append(" gap ");
                for (var index = 0; index < required.Length; index++)
                {
                    if (index > 0)
                        body.Append(" gap ").Append(Terminal(",")).Append(" gap ");
                    body.Append(Property(toolIndex, required[index]));
                }

                foreach (var property in optional)
                {
                    body.Append(" ( gap ")
                        .Append(Terminal(","))
                        .Append(" gap ")
                        .Append(Property(toolIndex, property))
                        .Append(" )?");
                }

                body.Append(" gap ").Append(Terminal("}"));
                return body.ToString();
            }

            var alternatives = new List<string>
            {
                $"{Terminal("{")} gap {Terminal("}")}",
            };

            for (var first = 0; first < optional.Length; first++)
            {
                var body = new StringBuilder();
                body.Append(Terminal("{")).Append(" gap ")
                    .Append(Property(toolIndex, optional[first]));
                for (var later = first + 1; later < optional.Length; later++)
                {
                    body.Append(" ( gap ")
                        .Append(Terminal(","))
                        .Append(" gap ")
                        .Append(Property(toolIndex, optional[later]))
                        .Append(" )?");
                }

                body.Append(" gap ").Append(Terminal("}"));
                alternatives.Add(body.ToString());
            }

            return string.Join(" | ", alternatives);
        }

        private string BuildArray(int toolIndex, SchemaNode schema)
        {
            var itemRule = EnsureSchemaRule(toolIndex, schema.Items!);
            if (schema.MaximumItems == 0)
                return $"{Terminal("[")} gap {Terminal("]")}";

            var nonEmpty = new StringBuilder()
                .Append(Terminal("["))
                .Append(" gap ")
                .Append(itemRule);
            var minimumTail = Math.Max(0, schema.MinimumItems - 1);
            var maximumTail = schema.MaximumItems - 1;
            if (maximumTail > 0)
            {
                var additionalItems = BoundedRepetition(
                    $"( gap {Terminal(",")} gap {itemRule} )",
                    minimumTail,
                    maximumTail,
                    $"t{toolIndex}-schema-{schema.Id}-additional-item");
                nonEmpty.Append(' ').Append(additionalItems);
            }

            nonEmpty.Append(" gap ").Append(Terminal("]"));
            return schema.MinimumItems == 0
                ? $"{Terminal("[")} gap {Terminal("]")} | {nonEmpty}"
                : nonEmpty.ToString();
        }

        private string BuildString(int toolIndex, SchemaNode schema)
        {
            _usesJsonCharacter = true;
            return QuotedString(
                schema.MinimumLength,
                schema.MaximumLength,
                $"t{toolIndex}-schema-{schema.Id}-character");
        }

        private string Property(int toolIndex, SchemaProperty property) =>
            $"{Terminal(JsonSerializer.Serialize(property.Name))} gap {Terminal(":")} gap "
            + EnsureSchemaRule(toolIndex, property.Schema);

        private string UseInteger()
        {
            _usesInteger = true;
            return "integer-value";
        }

        private string UseNumber()
        {
            _usesNumber = true;
            return "number-value";
        }

        private void AddRule(string name, string body)
        {
            if (!_ruleNames.Add(name))
            {
                throw new InvalidOperationException(
                    $"Grammar rule '{name}' was generated more than once. This indicates a "
                    + "package defect in schema-rule or bounded-repetition naming. No grammar is "
                    + "safe to attach; report the catalog and plan options to the package "
                    + "maintainer.");
            }

            _rules.Add((name, body));
        }

        private string QuotedString(int minimum, int maximum, string rulePrefix)
        {
            if (maximum == 0)
                return Terminal("\"\"");

            var characters = BoundedRepetition(
                "json-char",
                minimum,
                maximum,
                rulePrefix);
            return $"{Terminal("\"")} {characters} {Terminal("\"")}";
        }

        private string BoundedRepetition(
            string atom,
            int minimum,
            int maximum,
            string rulePrefix)
        {
            var required = ExactCount(atom, minimum, rulePrefix);
            var remaining = maximum - minimum;
            if (remaining == 0)
                return required;

            var optional = AtMost(atom, remaining, rulePrefix);
            return required.Length == 0 ? optional : $"{required} {optional}";
        }

        private string AtMost(string atom, int maximum, string rulePrefix)
        {
            if (maximum == 1)
                return $"( {atom} )?";

            var key = (atom, maximum);
            if (_atMostRules.TryGetValue(key, out var existing))
                return existing;

            var name = $"{rulePrefix}-up-to-{maximum}";
            _atMostRules.Add(key, name);
            var largestPower = HighestPowerOfTwo(maximum);
            var below = new List<string>();
            for (var power = largestPower / 2; power >= 1; power /= 2)
            {
                below.Add($"( {ExactPower(atom, power, rulePrefix)} )?");
            }

            var high = ExactPower(atom, largestPower, rulePrefix);
            var remainder = maximum - largestPower;
            if (remainder > 0)
                high += $" {AtMost(atom, remainder, rulePrefix)}";

            AddRule(name, $"{string.Join(" ", below)} | {high}");
            return name;
        }

        private string ExactCount(string atom, int count, string rulePrefix)
        {
            if (count == 0)
                return string.Empty;

            var parts = new List<string>();
            for (var power = HighestPowerOfTwo(count); power >= 1; power /= 2)
            {
                if ((count & power) != 0)
                    parts.Add(ExactPower(atom, power, rulePrefix));
            }

            return string.Join(" ", parts);
        }

        private string ExactPower(string atom, int power, string rulePrefix)
        {
            if (power == 1)
                return atom;

            var key = (atom, power);
            if (_exactPowerRules.TryGetValue(key, out var existing))
                return existing;

            var name = $"{rulePrefix}-exact-{power}";
            _exactPowerRules.Add(key, name);
            var half = ExactPower(atom, power / 2, rulePrefix);
            AddRule(name, $"{half} {half}");
            return name;
        }

        private static int HighestPowerOfTwo(int value)
        {
            var power = 1;
            while (power <= value / 2)
                power *= 2;
            return power;
        }

        private string UnsignedInteger(int maximumCharacters, string rulePrefix) =>
            maximumCharacters == 1
                ? "[0-9]"
                : $"{Terminal("0")} | [1-9] "
                  + BoundedRepetition(
                      "[0-9]",
                      0,
                      maximumCharacters - 1,
                      rulePrefix);

        private string Decimal(bool negative, int maximumCharacters, string rulePrefix)
        {
            var availableDigits = maximumCharacters - (negative ? 2 : 1);
            var wholeDigits = (availableDigits + 1) / 2;
            var fractionDigits = availableDigits - wholeDigits;
            var prefix = negative ? $"{Terminal("-")} " : string.Empty;
            return $"{prefix}( {UnsignedInteger(wholeDigits, $"{rulePrefix}-whole")} ) "
                   + $"{Terminal(".")} "
                   + BoundedRepetition(
                       "[0-9]",
                       1,
                       fractionDigits,
                       $"{rulePrefix}-fraction");
        }

        private static string Terminal(string value)
        {
            var builder = new StringBuilder(value.Length + 2).Append('"');
            foreach (var character in value)
            {
                if (character is '"' or '\\')
                    builder.Append('\\');
                builder.Append(character);
            }

            return builder.Append('"').ToString();
        }
    }
}

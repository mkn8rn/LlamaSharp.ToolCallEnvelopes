using System.Globalization;
using System.Text;

namespace LlamaSharp.ToolCallEnvelopes;

/// <summary>
/// Converts a conservative subset of regular expressions into a GBNF
/// body that matches the same language when wrapped in JSON string
/// delimiters. Used by <see cref="LlamaSharpJsonSchemaConverter"/> to
/// honour the <c>pattern</c> keyword without pulling in a full regex
/// engine.
/// </summary>
/// <remarks>
/// <para>
/// Supported surface: literal ASCII characters, character classes
/// <c>[abc]</c> with optional <c>^</c> negation and single-char
/// ranges, the dot metachar (any valid unescaped JSON character),
/// quantifiers <c>? * + {n} {n,} {n,m}</c> with numeric bounds no greater
/// than 10,000, alternation with top-level
/// <c>|</c>, non-capturing groups <c>(?:…)</c> and simple capturing
/// groups <c>(…)</c>, and the anchors <c>^</c> and <c>$</c> which are
/// implicit anyway because the grammar matches the full string.
/// </para>
/// <para>
/// Anything else — backreferences, lookarounds, named groups, inline
/// flags, unicode properties, the <c>\d\w\s</c> shorthand classes and
/// their negations — returns <c>false</c> so the caller can fall back
/// to the generic string rule.
/// </para>
/// </remarks>
public static class LlamaSharpRegexToGrammar
{
    private const int MaxQuantifierBound = 10_000;

    /// <summary>
    /// Attempts to convert <paramref name="pattern"/> to a GBNF body
    /// matching the same language surrounded by JSON string delimiters.
    /// </summary>
    /// <returns><c>true</c> on success; otherwise <c>false</c>.</returns>
    public static bool TryConvert(string pattern, out string grammarBody)
    {
        grammarBody = string.Empty;
        if (string.IsNullOrEmpty(pattern))
            return false;

        try
        {
            _ = new System.Text.RegularExpressions.Regex(
                pattern,
                System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException)
        {
            return false;
        }

        // Strip anchors — the outer rule always matches the full string.
        var span = pattern;
        if (span.StartsWith('^')) span = span[1..];
        if (span.EndsWith('$')) span = span[..^1];

        try
        {
            var parser = new Parser(span);
            var body = parser.ParseAlternation();
            if (!parser.AtEnd) return false;
            grammarBody = "\"\\\"\" " + body + " \"\\\"\"";
            return true;
        }
        catch (ConversionFailed)
        {
            return false;
        }
    }

    private sealed class ConversionFailed : Exception { }

    private ref struct Parser
    {
        private readonly ReadOnlySpan<char> _src;
        private int _pos;

        public Parser(string src)
        {
            _src = src.AsSpan();
            _pos = 0;
        }

        public bool AtEnd => _pos >= _src.Length;

        public string ParseAlternation()
        {
            var branches = new List<string> { ParseConcatenation() };
            while (!AtEnd && _src[_pos] == '|')
            {
                _pos++;
                branches.Add(ParseConcatenation());
            }
            return branches.Count == 1 ? branches[0] : "( " + string.Join(" | ", branches) + " )";
        }

        private string ParseConcatenation()
        {
            var sb = new StringBuilder();
            while (!AtEnd)
            {
                var c = _src[_pos];
                if (c == '|' || c == ')') break;
                var atom = ParseAtom();
                var quantified = ParseQuantifier(atom);
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(quantified);
            }
            return sb.Length == 0 ? "\"\"" : sb.ToString();
        }

        private string ParseQuantifier(string atom)
        {
            if (AtEnd) return atom;
            var c = _src[_pos];
            switch (c)
            {
                case '?': _pos++; return atom + "?";
                case '*': _pos++; return atom + "*";
                case '+': _pos++; return atom + "+";
                case '{':
                    _pos++;
                    var numsStart = _pos;
                    while (!AtEnd && _src[_pos] != '}') _pos++;
                    if (AtEnd) throw new ConversionFailed();
                    var inside = _src.Slice(numsStart, _pos - numsStart);
                    if (!TryParseQuantifier(inside, out var quantifier))
                        throw new ConversionFailed();
                    _pos++; // consume '}'
                    return atom + quantifier;
                default:
                    return atom;
            }
        }

        private string ParseAtom()
        {
            if (AtEnd) throw new ConversionFailed();
            var c = _src[_pos];
            switch (c)
            {
                case '(':
                    return ParseGroup();
                case '[':
                    return ParseCharClass();
                case '.':
                    _pos++;
                    return "[^\"\\\\\\x00-\\x1F]";
                case '\\':
                    return ParseEscape();
                case '$' or '^':
                    // Mid-expression anchors — unsupported.
                    throw new ConversionFailed();
                default:
                    if (!IsSafeLiteral(c))
                        throw new ConversionFailed();
                    _pos++;
                    return "\"" + EscapeLiteral(c) + "\"";
            }
        }

        private string ParseGroup()
        {
            _pos++; // consume '('
            // Allow (?: but reject any other (? form.
            if (!AtEnd && _src[_pos] == '?')
            {
                if (_pos + 1 >= _src.Length || _src[_pos + 1] != ':')
                    throw new ConversionFailed();
                _pos += 2;
            }
            var inner = ParseAlternation();
            if (AtEnd || _src[_pos] != ')') throw new ConversionFailed();
            _pos++;
            return "( " + inner + " )";
        }

        private string ParseCharClass()
        {
            _pos++; // consume '['
            var negated = !AtEnd && _src[_pos] == '^';
            if (negated) _pos++;

            var sb = new StringBuilder();
            sb.Append('[');
            if (negated) sb.Append('^');
            if (negated)
            {
                // A negated source class also excludes characters that cannot
                // appear unescaped inside the surrounding JSON string.
                sb.Append('"');
                sb.Append(@"\\");
                sb.Append(@"\x00-\x1F");
            }

            while (!AtEnd && _src[_pos] != ']')
            {
                var c = _src[_pos];
                if (c == '\\')
                {
                    if (_pos + 1 >= _src.Length) throw new ConversionFailed();
                    var esc = _src[_pos + 1];
                    if (!IsSafeEscapeInClass(esc)) throw new ConversionFailed();
                    sb.Append('\\');
                    sb.Append(esc);
                    _pos += 2;
                    continue;
                }
                if (!IsSafeClassChar(c)) throw new ConversionFailed();
                sb.Append(c);
                _pos++;
            }
            if (AtEnd) throw new ConversionFailed();
            _pos++; // consume ']'
            sb.Append(']');
            return sb.ToString();
        }

        private static bool TryParseQuantifier(
            ReadOnlySpan<char> source,
            out string quantifier)
        {
            quantifier = string.Empty;
            var comma = source.IndexOf(',');
            if (comma < 0)
            {
                if (!TryParseBound(source, out var exact))
                    return false;

                quantifier = $"{{{exact.ToString(CultureInfo.InvariantCulture)}}}";
                return true;
            }

            if (source[(comma + 1)..].IndexOf(',') >= 0
                || !TryParseBound(source[..comma], out var minimum))
            {
                return false;
            }

            var maximumSource = source[(comma + 1)..];
            if (maximumSource.IsEmpty)
            {
                quantifier = $"{{{minimum.ToString(CultureInfo.InvariantCulture)},}}";
                return true;
            }

            if (!TryParseBound(maximumSource, out var maximum)
                || maximum < minimum)
            {
                return false;
            }

            quantifier = $"{{{minimum.ToString(CultureInfo.InvariantCulture)},{maximum.ToString(CultureInfo.InvariantCulture)}}}";
            return true;
        }

        private static bool TryParseBound(ReadOnlySpan<char> source, out int value)
        {
            value = 0;
            if (source.IsEmpty)
                return false;

            foreach (var character in source)
            {
                if (character is < '0' or > '9')
                    return false;
            }

            return int.TryParse(
                source,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out value)
                && value <= MaxQuantifierBound;
        }

        private string ParseEscape()
        {
            _pos++; // consume backslash
            if (AtEnd) throw new ConversionFailed();
            var c = _src[_pos];
            _pos++;
            // Only allow escapes to literal metacharacters. No \d\w\s
            // shorthands — grammar has no Unicode category machinery.
            if (!IsEscapableMeta(c)) throw new ConversionFailed();
            return "\"" + EscapeLiteral(c) + "\"";
        }

        private static bool IsSafeLiteral(char c) =>
            c >= 0x20 && c < 0x7F && c != '"' && c != '\\'
            && c != '(' && c != ')' && c != '[' && c != ']'
            && c != '{' && c != '}' && c != '|' && c != '*'
            && c != '+' && c != '?' && c != '.' && c != '^'
            && c != '$';

        private static bool IsSafeClassChar(char c) =>
            c >= 0x20 && c < 0x7F && c != '"' && c != '\\' && c != ']';

        private static bool IsSafeEscapeInClass(char c) =>
            c == '\\' || c == ']' || c == '^' || c == '-' || c == '/';

        private static bool IsEscapableMeta(char c) =>
            c == '.' || c == '\\' || c == '/' || c == '(' || c == ')'
            || c == '[' || c == ']' || c == '{' || c == '}' || c == '|'
            || c == '*' || c == '+' || c == '?' || c == '^' || c == '$'
            || c == '-';

        private static string EscapeLiteral(char c) => c switch
        {
            '\\' => "\\\\",
            '"' => "\\\"",
            _ => c.ToString(),
        };
    }
}

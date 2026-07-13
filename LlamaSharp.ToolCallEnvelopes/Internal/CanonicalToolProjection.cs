using System.Text;

namespace LlamaSharp.ToolCallEnvelopes.Internal;

internal sealed class CanonicalToolProjection
{
    private readonly StringBuilder _token = new();
    private ToolPhase _phase;
    private bool _tokenEscaped;
    private int _callIndex;
    private int _argumentDepth;
    private bool _argumentString;
    private bool _argumentEscaped;
    private bool _disabled;

    internal IReadOnlyList<ToolEnvelopeStreamUpdate> Feed(string fragment)
    {
        if (_disabled || fragment.Length == 0)
            return Array.Empty<ToolEnvelopeStreamUpdate>();

        var argumentFragments = new Dictionary<int, StringBuilder>();
        foreach (var character in fragment)
        {
            if (!Process(character, argumentFragments))
            {
                _disabled = true;
                break;
            }
        }

        return CreateUpdates(argumentFragments);
    }

    private bool Process(char character, Dictionary<int, StringBuilder> argumentFragments)
    {
        if (_phase == ToolPhase.Arguments)
            return ReadArgumentCharacter(character, argumentFragments);

        if (character is ' ' or '\t' or '\r' or '\n')
            return true;

        return _phase switch
        {
            ToolPhase.RootStart => Expect(character, '{', ToolPhase.RootPropertyStart),
            ToolPhase.RootPropertyStart => StartToken(character, ToolPhase.RootProperty),
            ToolPhase.RootProperty => ReadToken(character, "tool_calls", ToolPhase.RootColon),
            ToolPhase.RootColon => Expect(character, ':', ToolPhase.ArrayStart),
            ToolPhase.ArrayStart => Expect(character, '[', ToolPhase.CallStart),
            ToolPhase.CallStart => Expect(character, '{', ToolPhase.NamePropertyStart),
            ToolPhase.NamePropertyStart => StartToken(character, ToolPhase.NameProperty),
            ToolPhase.NameProperty => ReadToken(character, "name", ToolPhase.NameColon),
            ToolPhase.NameColon => Expect(character, ':', ToolPhase.NameValueStart),
            ToolPhase.NameValueStart => StartToken(character, ToolPhase.NameValue),
            ToolPhase.NameValue => ReadToken(character, expected: null, ToolPhase.CallComma),
            ToolPhase.CallComma => Expect(character, ',', ToolPhase.ArgumentsPropertyStart),
            ToolPhase.ArgumentsPropertyStart => StartToken(character, ToolPhase.ArgumentsProperty),
            ToolPhase.ArgumentsProperty => ReadToken(
                character,
                "arguments",
                ToolPhase.ArgumentsColon),
            ToolPhase.ArgumentsColon => Expect(character, ':', ToolPhase.ArgumentsStart),
            ToolPhase.ArgumentsStart => StartArguments(character, argumentFragments),
            ToolPhase.CallEnd => Expect(character, '}', ToolPhase.AfterCall),
            ToolPhase.AfterCall => FinishCall(character),
            ToolPhase.RootEnd => Expect(character, '}', ToolPhase.Done),
            ToolPhase.Done => false,
            _ => false,
        };
    }

    private bool ReadArgumentCharacter(
        char character,
        Dictionary<int, StringBuilder> argumentFragments)
    {
        if (!argumentFragments.TryGetValue(_callIndex, out var output))
        {
            output = new StringBuilder();
            argumentFragments.Add(_callIndex, output);
        }

        output.Append(character);
        if (_argumentString)
        {
            if (_argumentEscaped)
                _argumentEscaped = false;
            else if (character == '\\')
                _argumentEscaped = true;
            else if (character == '"')
                _argumentString = false;
            return true;
        }

        if (character == '"')
        {
            _argumentString = true;
        }
        else if (character is '{' or '[')
        {
            _argumentDepth++;
        }
        else if (character is '}' or ']')
        {
            _argumentDepth--;
            if (_argumentDepth == 0)
                _phase = ToolPhase.CallEnd;
        }

        return _argumentDepth >= 0;
    }

    private bool StartArguments(
        char character,
        Dictionary<int, StringBuilder> argumentFragments)
    {
        if (character != '{')
            return false;
        _phase = ToolPhase.Arguments;
        _argumentDepth = 1;
        argumentFragments[_callIndex] = new StringBuilder().Append(character);
        return true;
    }

    private bool FinishCall(char character)
    {
        if (character == ',')
        {
            _callIndex++;
            _phase = ToolPhase.CallStart;
            return true;
        }

        if (character == ']')
        {
            _phase = ToolPhase.RootEnd;
            return true;
        }

        return false;
    }

    private bool Expect(char actual, char expected, ToolPhase next)
    {
        if (actual != expected)
            return false;
        _phase = next;
        return true;
    }

    private bool StartToken(char character, ToolPhase tokenPhase)
    {
        if (character != '"')
            return false;
        _token.Clear();
        _tokenEscaped = false;
        _phase = tokenPhase;
        return true;
    }

    private bool ReadToken(char character, string? expected, ToolPhase next)
    {
        if (_tokenEscaped)
            return false;
        if (character == '\\')
        {
            _tokenEscaped = true;
            return false;
        }

        if (character == '"')
        {
            if (expected is not null
                && !string.Equals(_token.ToString(), expected, StringComparison.Ordinal))
            {
                return false;
            }

            _phase = next;
            return true;
        }

        if (character < 0x20)
            return false;
        _token.Append(character);
        return true;
    }

    private static IReadOnlyList<ToolEnvelopeStreamUpdate> CreateUpdates(
        Dictionary<int, StringBuilder> fragments) =>
        fragments
            .Where(item => item.Value.Length > 0)
            .OrderBy(item => item.Key)
            .Select(item => (ToolEnvelopeStreamUpdate)new ToolEnvelopeStreamUpdate.ToolArgumentsDelta(
                item.Key,
                item.Value.ToString()))
            .ToArray();

    private enum ToolPhase
    {
        RootStart,
        RootPropertyStart,
        RootProperty,
        RootColon,
        ArrayStart,
        CallStart,
        NamePropertyStart,
        NameProperty,
        NameColon,
        NameValueStart,
        NameValue,
        CallComma,
        ArgumentsPropertyStart,
        ArgumentsProperty,
        ArgumentsColon,
        ArgumentsStart,
        Arguments,
        CallEnd,
        AfterCall,
        RootEnd,
        Done,
    }
}

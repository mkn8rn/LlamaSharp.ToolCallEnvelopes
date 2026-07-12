using System.Text;

namespace LlamaSharp.ToolCallEnvelopes;

/// <summary>
/// Detects envelope combinations that are already impossible while a model is
/// still streaming JSON. It never repairs or rewrites the source stream.
/// </summary>
public sealed class LlamaSharpToolEnvelopeStreamValidator
{
    private readonly ToolEnvelopeMode _mode;
    private readonly bool _enforceSemanticValidation;
    private readonly StringBuilder _buffer = new();
    private readonly List<char> _containers = [];
    private readonly StringBuilder _capturedString = new();
    private StringCapture _capture;
    private bool _inString;
    private bool _escape;
    private int _unicodeDigitsRemaining;
    private int _unicodeValue;
    private bool _expectRootProperty;
    private bool _expectRootValue;
    private string? _rootProperty;
    private string? _modeValue;
    private string? _activeCallArray;
    private int _activeCallArrayDepth;
    private bool _legacyCallArrayHasElement;
    private bool _callArrayHasElement;
    private bool _sawText;
    private bool _sawRefusal;
    private bool _sawNewToolCalls;
    private bool _sawLegacyCalls;

    public LlamaSharpToolEnvelopeStreamValidator(
        ToolEnvelopeMode mode = ToolEnvelopeMode.Inferred)
        : this(mode, enforceSemanticValidation: true)
    {
    }

    internal LlamaSharpToolEnvelopeStreamValidator(
        ToolEnvelopeMode mode,
        bool enforceSemanticValidation)
    {
        _mode = mode;
        _enforceSemanticValidation = enforceSemanticValidation;
    }

    public string RawEnvelope => _buffer.ToString();

    internal string? DeclaredMode => _modeValue;
    internal bool SawText => _sawText;
    internal bool SawRefusal => _sawRefusal;
    internal bool SawNewToolCalls => _sawNewToolCalls;
    internal bool SawLegacyCalls => _sawLegacyCalls;
    internal bool LegacyCallArrayHasElement => _legacyCallArrayHasElement;

    /// <summary>
    /// Adds a raw model fragment and throws as soon as a semantic contradiction
    /// can be proven from the tokens received so far.
    /// </summary>
    public void Feed(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        _buffer.Append(token);
        foreach (var character in token)
            ScanCharacter(character);
    }

    private void ScanCharacter(char character)
    {
        if (_inString)
        {
            ScanStringCharacter(character);
            return;
        }

        switch (character)
        {
            case '"':
                StartString();
                return;

            case '{':
                if (_containers.Count == 0)
                {
                    _containers.Add('{');
                    _expectRootProperty = true;
                    return;
                }

                if (IsDirectCallArray())
                {
                    _callArrayHasElement = true;
                    _legacyCallArrayHasElement |= _activeCallArray == "calls";
                    ValidateSemanticState();
                }

                MarkRootValueStarted();
                _containers.Add('{');
                return;

            case '[':
                var callArray = IsRootObject && _expectRootValue
                                && (_rootProperty == "calls" || _rootProperty == "tool_calls")
                    ? _rootProperty
                    : null;
                MarkRootValueStarted();
                _containers.Add('[');
                if (callArray is not null)
                {
                    _activeCallArray = callArray;
                    _activeCallArrayDepth = _containers.Count;
                }
                return;

            case ']':
                if (_activeCallArray is not null
                    && _containers.Count == _activeCallArrayDepth
                    && _containers[^1] == '[')
                {
                    _activeCallArray = null;
                    _activeCallArrayDepth = 0;
                }
                PopContainer('[');
                return;

            case '}':
                PopContainer('{');
                if (_containers.Count == 0)
                {
                    _expectRootProperty = false;
                    _expectRootValue = false;
                    _rootProperty = null;
                }
                return;

            case ':' when IsRootObject && _rootProperty is not null:
                _expectRootValue = true;
                return;

            case ',' when IsRootObject:
                _rootProperty = null;
                _expectRootValue = false;
                _expectRootProperty = true;
                return;

            default:
                if (!char.IsWhiteSpace(character))
                    MarkRootValueStarted();
                return;
        }
    }

    private void StartString()
    {
        _inString = true;
        _escape = false;
        _unicodeDigitsRemaining = 0;
        _unicodeValue = 0;
        _capturedString.Clear();
        _capture = StringCapture.None;

        if (!IsRootObject)
            return;

        if (_expectRootProperty)
        {
            _capture = StringCapture.RootProperty;
            return;
        }

        if (_expectRootValue)
        {
            if (_rootProperty == "mode")
                _capture = StringCapture.ModeValue;
            MarkRootValueStarted();
        }
    }

    private void ScanStringCharacter(char character)
    {
        if (_unicodeDigitsRemaining > 0)
        {
            var digit = HexValue(character);
            if (digit < 0)
            {
                _unicodeDigitsRemaining = 0;
                _unicodeValue = 0;
                AppendCaptured(character);
                return;
            }

            _unicodeValue = (_unicodeValue << 4) | digit;
            _unicodeDigitsRemaining--;
            if (_unicodeDigitsRemaining == 0)
            {
                AppendCaptured((char)_unicodeValue);
                _unicodeValue = 0;
            }
            return;
        }

        if (_escape)
        {
            _escape = false;
            if (character == 'u')
            {
                _unicodeDigitsRemaining = 4;
                _unicodeValue = 0;
                return;
            }

            AppendCaptured(character switch
            {
                'b' => '\b',
                'f' => '\f',
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                _ => character,
            });
            return;
        }

        if (character == '\\')
        {
            _escape = true;
            return;
        }

        if (character != '"')
        {
            AppendCaptured(character);
            return;
        }

        _inString = false;
        if (_capture == StringCapture.RootProperty)
        {
            _rootProperty = _capturedString.ToString();
            _expectRootProperty = false;
            _sawText |= _rootProperty == "text";
            _sawRefusal |= _rootProperty == "refusal";
            _sawNewToolCalls |= _rootProperty == "tool_calls";
            _sawLegacyCalls |= _rootProperty == "calls";
        }
        else if (_capture == StringCapture.ModeValue)
        {
            _modeValue = _capturedString.ToString();
        }

        _capture = StringCapture.None;
        ValidateSemanticState();
    }

    private void AppendCaptured(char character)
    {
        if (_capture != StringCapture.None)
            _capturedString.Append(character);
    }

    private void MarkRootValueStarted()
    {
        if (IsRootObject && _expectRootValue)
            _expectRootValue = false;
    }

    private bool IsDirectCallArray() =>
        _activeCallArray is not null
        && _containers.Count == _activeCallArrayDepth
        && _containers[^1] == '[';

    private bool IsRootObject =>
        _containers.Count == 1 && _containers[0] == '{';

    private void PopContainer(char expected)
    {
        if (_containers.Count > 0 && _containers[^1] == expected)
            _containers.RemoveAt(_containers.Count - 1);
    }

    private void ValidateSemanticState()
    {
        if (!_enforceSemanticValidation)
            return;

        if (_mode == ToolEnvelopeMode.StrictDeclared
            && _legacyCallArrayHasElement
            && (_modeValue is "message" or "refusal"))
        {
            throw new LlamaSharpToolEnvelopeException(
                "EnvelopeModePayloadMismatch",
                $"The model declared mode \"{_modeValue}\", but the payload contains a tool call at $.calls. Use mode \"tool_calls\", or remove the call.",
                _buffer.ToString(),
                "$.calls");
        }

        if (_mode == ToolEnvelopeMode.Inferred)
        {
            if (_sawText && _sawNewToolCalls)
            {
                throw new LlamaSharpToolEnvelopeException(
                    "PayloadConflict",
                    "An inferred envelope cannot combine the final-text payload with a new tool_calls payload.",
                    _buffer.ToString(),
                    "$.tool_calls");
            }

            if (_sawRefusal && (_sawText || _callArrayHasElement))
            {
                throw new LlamaSharpToolEnvelopeException(
                    "PayloadConflict",
                    "An inferred refusal envelope cannot also contain text or tool calls.",
                    _buffer.ToString(),
                    _sawText ? "$.text" : "$.calls");
            }
        }
    }

    private static int HexValue(char character) => character switch
    {
        >= '0' and <= '9' => character - '0',
        >= 'a' and <= 'f' => character - 'a' + 10,
        >= 'A' and <= 'F' => character - 'A' + 10,
        _ => -1,
    };

    private enum StringCapture
    {
        None,
        RootProperty,
        ModeValue,
    }
}

using System.Globalization;
using System.Text;

namespace LlamaSharp.ToolCallEnvelopes;

/// <summary>
/// Incrementally parses envelope tokens into text or tool-call deltas.
/// </summary>
public sealed class LlamaSharpToolEnvelopeStreamParser
{
    private readonly ToolEnvelopeParserOptions _options;
    private readonly LlamaSharpToolEnvelopeStreamValidator _streamState;
    private readonly StringBuilder _buffer = new();
    private readonly EnvelopeToolCallStreamWalker _toolWalker = new();
    private readonly EnvelopeTextStreamWalker _textWalker = new("text");
    private readonly EnvelopeTextStreamWalker _refusalWalker = new("refusal");
    private string? _mode;

    public LlamaSharpToolEnvelopeStreamParser(
        ToolEnvelopeParserOptions? options = null,
        ToolEnvelopeStreamValidation validation = ToolEnvelopeStreamValidation.Off)
    {
        _options = options ?? new ToolEnvelopeParserOptions();
        _streamState = new LlamaSharpToolEnvelopeStreamValidator(
            _options.EnvelopeMode,
            enforceSemanticValidation: validation == ToolEnvelopeStreamValidation.Strict);
    }

    public string RawEnvelope => _buffer.ToString();

    public IReadOnlyList<ToolEnvelopeStreamChunk> Feed(string token)
    {
        ArgumentNullException.ThrowIfNull(token);

        var chunks = new List<ToolEnvelopeStreamChunk>();
        _buffer.Append(token);
        _streamState.Feed(token);

        if (_options.EnvelopeMode == ToolEnvelopeMode.Inferred
            && _mode == LlamaSharpToolEnvelopeParser.MessageMode
            && _streamState.LegacyCallArrayHasElement)
        {
            _mode = LlamaSharpToolEnvelopeParser.ToolCallsMode;
            foreach (var delta in _toolWalker.Feed(_buffer.ToString()))
                chunks.Add(ToolEnvelopeStreamChunk.ToolCall(delta));
            return chunks;
        }

        if (_mode is null)
        {
            var text = _buffer.ToString();
            if (_streamState.DeclaredMode == LlamaSharpToolEnvelopeParser.ToolCallsMode
                || _streamState.SawNewToolCalls
                || (_options.EnvelopeMode == ToolEnvelopeMode.Inferred && _streamState.SawLegacyCalls))
            {
                _mode = LlamaSharpToolEnvelopeParser.ToolCallsMode;
                foreach (var delta in _toolWalker.Feed(text))
                    chunks.Add(ToolEnvelopeStreamChunk.ToolCall(delta));
                return chunks;
            }

            if (_streamState.SawRefusal &&
                _options.EnvelopeMode == ToolEnvelopeMode.Inferred)
            {
                _mode = LlamaSharpToolEnvelopeParser.RefusalMode;
                foreach (var delta in _refusalWalker.Feed(text))
                    chunks.Add(ToolEnvelopeStreamChunk.Text(delta));
                return chunks;
            }

            if (_streamState.DeclaredMode == LlamaSharpToolEnvelopeParser.MessageMode
                || (_options.EnvelopeMode == ToolEnvelopeMode.Inferred && _streamState.SawText))
            {
                _mode = LlamaSharpToolEnvelopeParser.MessageMode;
                foreach (var delta in _textWalker.Feed(text))
                    chunks.Add(ToolEnvelopeStreamChunk.Text(delta));
                return chunks;
            }

            if (_streamState.DeclaredMode == LlamaSharpToolEnvelopeParser.RefusalMode
                || _streamState.SawRefusal)
            {
                _mode = LlamaSharpToolEnvelopeParser.RefusalMode;
                foreach (var delta in _textWalker.Feed(text))
                    chunks.Add(ToolEnvelopeStreamChunk.Text(delta));
                return chunks;
            }

            return chunks;
        }

        if (_mode == LlamaSharpToolEnvelopeParser.ToolCallsMode)
        {
            foreach (var delta in _toolWalker.Feed(token))
                chunks.Add(ToolEnvelopeStreamChunk.ToolCall(delta));
        }
        else if (_mode == LlamaSharpToolEnvelopeParser.RefusalMode
                 && _options.EnvelopeMode == ToolEnvelopeMode.Inferred
                 && _streamState.SawRefusal)
        {
            foreach (var delta in _refusalWalker.Feed(token))
                chunks.Add(ToolEnvelopeStreamChunk.Text(delta));
        }
        else
        {
            foreach (var delta in _textWalker.Feed(token))
                chunks.Add(ToolEnvelopeStreamChunk.Text(delta));
        }

        return chunks;
    }

    public ToolEnvelopeResult Complete() =>
        LlamaSharpToolEnvelopeParser.Parse(_buffer.ToString(), _options);

    private sealed class EnvelopeTextStreamWalker
    {
        private enum Phase
        {
            SeekingTextKey,
            AfterTextKey,
            AwaitingTextString,
            ReadingText,
            Done,
        }

        private Phase _phase = Phase.SeekingTextKey;
        private readonly StringBuilder _seek = new();
        private readonly string _propertyName;
        private bool _escape;
        private bool _unicodeEscape;
        private readonly StringBuilder _unicode = new(4);

        public EnvelopeTextStreamWalker(string propertyName)
        {
            _propertyName = propertyName;
        }

        public IEnumerable<string> Feed(string token)
        {
            var chunks = new List<string>();
            foreach (var ch in token)
                FeedChar(ch, chunks);
            return chunks;
        }

        private void FeedChar(char ch, List<string> chunks)
        {
            switch (_phase)
            {
                case Phase.SeekingTextKey:
                    _seek.Append(ch);
                    if (_seek.Length > 16)
                        _seek.Remove(0, _seek.Length - 16);
                    if (_seek.ToString().Contains($"\"{_propertyName}\"", StringComparison.Ordinal))
                    {
                        _seek.Clear();
                        _phase = Phase.AfterTextKey;
                    }
                    break;

                case Phase.AfterTextKey:
                    if (ch == ':')
                        _phase = Phase.AwaitingTextString;
                    else if (!char.IsWhiteSpace(ch))
                        _phase = Phase.SeekingTextKey;
                    break;

                case Phase.AwaitingTextString:
                    if (ch == '"')
                        _phase = Phase.ReadingText;
                    else if (!char.IsWhiteSpace(ch))
                        _phase = Phase.SeekingTextKey;
                    break;

                case Phase.ReadingText:
                    if (_unicodeEscape)
                    {
                        _unicode.Append(ch);
                        if (_unicode.Length == 4)
                        {
                            if (ushort.TryParse(
                                    _unicode.ToString(),
                                    NumberStyles.HexNumber,
                                    CultureInfo.InvariantCulture,
                                    out var code))
                            {
                                chunks.Add(((char)code).ToString());
                            }
                            else
                            {
                                chunks.Add("\\u" + _unicode);
                            }
                            _unicode.Clear();
                            _unicodeEscape = false;
                        }
                        break;
                    }

                    if (_escape)
                    {
                        _escape = false;
                        switch (ch)
                        {
                            case '"': chunks.Add("\""); break;
                            case '\\': chunks.Add("\\"); break;
                            case '/': chunks.Add("/"); break;
                            case 'b': chunks.Add("\b"); break;
                            case 'f': chunks.Add("\f"); break;
                            case 'n': chunks.Add("\n"); break;
                            case 'r': chunks.Add("\r"); break;
                            case 't': chunks.Add("\t"); break;
                            case 'u':
                                _unicodeEscape = true;
                                break;
                            default:
                                chunks.Add("\\" + ch);
                                break;
                        }
                        break;
                    }

                    if (ch == '\\')
                    {
                        _escape = true;
                    }
                    else if (ch == '"')
                    {
                        _phase = Phase.Done;
                    }
                    else
                    {
                        chunks.Add(ch.ToString());
                    }
                    break;

                case Phase.Done:
                    break;
            }
        }
    }

    private sealed class EnvelopeToolCallStreamWalker
    {
        private enum Phase
        {
            SeekingCallsArray,
            BetweenCalls,
            InCallSeekKey,
            ReadingKey,
            AfterKey,
            ReadingIdValue,
            ReadingNameValue,
            ReadingArgsValue,
            SkippingUnknownValue,
            AfterValue,
            Done,
        }

        private Phase _phase = Phase.SeekingCallsArray;
        private int _callIndex = -1;
        private readonly StringBuilder _seek = new();
        private readonly StringBuilder _keyBuffer = new();
        private bool _keyEscape;
        private readonly StringBuilder _valueBuffer = new();
        private bool _valueEscape;
        private int _argsDepth;
        private bool _argsInString;
        private bool _argsEscape;
        private readonly StringBuilder _argsPending = new();
        private int _skipDepth;
        private bool _skipInString;
        private bool _skipEscape;
        private bool _skipStarted;
        private char _skipTerminatorType;
        private bool _awaitingStringOpen;
        private bool _awaitingArgsOpen;

        public IEnumerable<ToolCallDelta> Feed(string token)
        {
            var deltas = new List<ToolCallDelta>();
            foreach (var ch in token)
                FeedChar(ch, deltas);
            return deltas;
        }

        private void FeedChar(char ch, List<ToolCallDelta> deltas)
        {
            switch (_phase)
            {
                case Phase.SeekingCallsArray:
                    _seek.Append(ch);
                    if (_seek.Length > 32)
                        _seek.Remove(0, _seek.Length - 32);
                    var s = _seek.ToString();
                    var idx = s.IndexOf("\"calls\"", StringComparison.Ordinal);
                    var toolCallsIdx = s.IndexOf("\"tool_calls\"", StringComparison.Ordinal);
                    if (toolCallsIdx >= 0 && (idx < 0 || toolCallsIdx < idx))
                        idx = toolCallsIdx;
                    if (idx >= 0)
                    {
                        var propertyLength = s.AsSpan(idx).StartsWith("\"tool_calls\"", StringComparison.Ordinal)
                            ? "\"tool_calls\"".Length
                            : "\"calls\"".Length;
                        var rest = s[(idx + propertyLength)..];
                        var colon = rest.IndexOf(':');
                        if (colon >= 0)
                        {
                            var bracket = rest[(colon + 1)..].IndexOf('[');
                            if (bracket >= 0)
                            {
                                _phase = Phase.BetweenCalls;
                                _seek.Clear();
                            }
                        }
                    }
                    break;

                case Phase.BetweenCalls:
                    if (ch == '{')
                    {
                        _callIndex++;
                        _phase = Phase.InCallSeekKey;
                    }
                    else if (ch == ']')
                    {
                        _phase = Phase.Done;
                    }
                    break;

                case Phase.InCallSeekKey:
                    if (ch == '"')
                    {
                        _keyBuffer.Clear();
                        _keyEscape = false;
                        _phase = Phase.ReadingKey;
                    }
                    else if (ch == '}')
                    {
                        _phase = Phase.BetweenCalls;
                    }
                    break;

                case Phase.ReadingKey:
                    if (_keyEscape)
                    {
                        _keyBuffer.Append(ch);
                        _keyEscape = false;
                    }
                    else if (ch == '\\')
                    {
                        _keyBuffer.Append(ch);
                        _keyEscape = true;
                    }
                    else if (ch == '"')
                    {
                        _phase = Phase.AfterKey;
                    }
                    else
                    {
                        _keyBuffer.Append(ch);
                    }
                    break;

                case Phase.AfterKey:
                    if (ch == ':')
                    {
                        var key = _keyBuffer.ToString();
                        _valueBuffer.Clear();
                        _valueEscape = false;
                        _argsDepth = 0;
                        _argsInString = false;
                        _argsEscape = false;
                        _argsPending.Clear();
                        _skipDepth = 0;
                        _skipInString = false;
                        _skipEscape = false;
                        _skipStarted = false;
                        _skipTerminatorType = (char)0;
                        _phase = key switch
                        {
                            "id" => Phase.ReadingIdValue,
                            "name" => Phase.ReadingNameValue,
                            "args" => Phase.ReadingArgsValue,
                            "arguments" => Phase.ReadingArgsValue,
                            _ => Phase.SkippingUnknownValue,
                        };
                        _awaitingStringOpen = _phase is Phase.ReadingIdValue or Phase.ReadingNameValue;
                        _awaitingArgsOpen = _phase == Phase.ReadingArgsValue;
                    }
                    break;

                case Phase.ReadingIdValue:
                case Phase.ReadingNameValue:
                    if (_awaitingStringOpen)
                    {
                        if (ch == '"')
                            _awaitingStringOpen = false;
                        break;
                    }

                    if (_valueEscape)
                    {
                        _valueBuffer.Append(ch);
                        _valueEscape = false;
                    }
                    else if (ch == '\\')
                    {
                        _valueBuffer.Append(ch);
                        _valueEscape = true;
                    }
                    else if (ch == '"')
                    {
                        var value = DecodeJsonString(_valueBuffer.ToString());
                        deltas.Add(_phase == Phase.ReadingIdValue
                            ? new ToolCallDelta(_callIndex, value, null, null)
                            : new ToolCallDelta(_callIndex, null, value, null));
                        _valueBuffer.Clear();
                        _phase = Phase.AfterValue;
                    }
                    else
                    {
                        _valueBuffer.Append(ch);
                    }
                    break;

                case Phase.ReadingArgsValue:
                    if (_awaitingArgsOpen)
                    {
                        if (ch == '{')
                        {
                            _awaitingArgsOpen = false;
                            _argsDepth = 1;
                            _argsPending.Append(ch);
                        }
                        break;
                    }

                    _argsPending.Append(ch);
                    if (_argsInString)
                    {
                        if (_argsEscape)
                            _argsEscape = false;
                        else if (ch == '\\')
                            _argsEscape = true;
                        else if (ch == '"')
                            _argsInString = false;
                    }
                    else
                    {
                        if (ch == '"')
                            _argsInString = true;
                        else if (ch == '{' || ch == '[')
                            _argsDepth++;
                        else if (ch == '}' || ch == ']')
                        {
                            _argsDepth--;
                            if (_argsDepth == 0)
                            {
                                var fragment = _argsPending.ToString();
                                _argsPending.Clear();
                                deltas.Add(new ToolCallDelta(_callIndex, null, null, fragment));
                                _phase = Phase.AfterValue;
                                break;
                            }
                        }
                    }

                    if (!_argsEscape && _argsPending.Length > 0)
                    {
                        var fragment = _argsPending.ToString();
                        _argsPending.Clear();
                        deltas.Add(new ToolCallDelta(_callIndex, null, null, fragment));
                    }
                    break;

                case Phase.SkippingUnknownValue:
                    SkipUnknownValue(ch, deltas);
                    break;

                case Phase.AfterValue:
                    if (ch == ',')
                        _phase = Phase.InCallSeekKey;
                    else if (ch == '}')
                        _phase = Phase.BetweenCalls;
                    break;

                case Phase.Done:
                    break;
            }
        }

        private void SkipUnknownValue(char ch, List<ToolCallDelta> deltas)
        {
            if (!_skipStarted)
            {
                if (char.IsWhiteSpace(ch)) return;
                _skipStarted = true;
                if (ch == '"')
                {
                    _skipTerminatorType = 's';
                    _skipInString = true;
                }
                else if (ch == '{' || ch == '[')
                {
                    _skipTerminatorType = 'o';
                    _skipDepth = 1;
                }
                else
                {
                    _skipTerminatorType = 'p';
                }
                return;
            }

            if (_skipTerminatorType == 's')
            {
                if (_skipEscape) _skipEscape = false;
                else if (ch == '\\') _skipEscape = true;
                else if (ch == '"') _phase = Phase.AfterValue;
            }
            else if (_skipTerminatorType == 'o')
            {
                if (_skipInString)
                {
                    if (_skipEscape) _skipEscape = false;
                    else if (ch == '\\') _skipEscape = true;
                    else if (ch == '"') _skipInString = false;
                }
                else
                {
                    if (ch == '"') _skipInString = true;
                    else if (ch == '{' || ch == '[') _skipDepth++;
                    else if (ch == '}' || ch == ']')
                    {
                        _skipDepth--;
                        if (_skipDepth == 0) _phase = Phase.AfterValue;
                    }
                }
            }
            else if (ch == ',' || ch == '}' || char.IsWhiteSpace(ch))
            {
                _phase = Phase.AfterValue;
                FeedChar(ch, deltas);
            }
        }

        private static string DecodeJsonString(string raw)
        {
            if (raw.Length == 0) return string.Empty;
            if (raw.IndexOf('\\') < 0) return raw;

            var sb = new StringBuilder(raw.Length);
            for (var i = 0; i < raw.Length; i++)
            {
                var c = raw[i];
                if (c != '\\' || i + 1 >= raw.Length)
                {
                    sb.Append(c);
                    continue;
                }

                var next = raw[++i];
                switch (next)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 < raw.Length
                            && ushort.TryParse(
                                raw.AsSpan(i + 1, 4),
                                NumberStyles.HexNumber,
                                CultureInfo.InvariantCulture,
                                out var code))
                        {
                            sb.Append((char)code);
                            i += 4;
                        }
                        else
                        {
                            sb.Append('\\').Append(next);
                        }
                        break;
                    default:
                        sb.Append('\\').Append(next);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}

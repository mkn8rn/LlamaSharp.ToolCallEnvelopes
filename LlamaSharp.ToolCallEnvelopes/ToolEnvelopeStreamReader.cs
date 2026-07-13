using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using LlamaSharp.ToolCallEnvelopes.Internal;

namespace LlamaSharp.ToolCallEnvelopes;

/// <summary>
/// A single-use, bounded stream reader. Updates remain provisional until completion succeeds.
/// </summary>
public sealed class ToolEnvelopeStreamReader
{
    private readonly ToolEnvelopeTurn _turn;
    private readonly StringBuilder _raw = new();
    private ProjectionKind _projection;
    private int _stringIndex;
    private bool _stringEscaped;
    private int _unicodeDigits;
    private int _unicodeValue;
    private char? _pendingHighSurrogate;
    private CanonicalToolProjection? _toolProjection;
    private ReaderState _state;
    private ToolEnvelopeError? _terminalError;
    private bool _completionInvoked;

    internal ToolEnvelopeStreamReader(ToolEnvelopeTurn turn) => _turn = turn;

    internal string RawOutput => _raw.ToString();

    /// <summary>
    /// Feeds one model fragment and returns coalesced provisional updates for that fragment.
    /// </summary>
    public IReadOnlyList<ToolEnvelopeStreamUpdate> Feed(string fragment)
    {
        Guard.NotNull(
            fragment,
            nameof(fragment),
            "A model stream fragment cannot be null. Yield non-null newly generated text; an empty "
            + "string is accepted when the adapter cannot avoid an empty fragment.");
        EnsureOpen();
        if (fragment.Length == 0)
            return Array.Empty<ToolEnvelopeStreamUpdate>();

        if (_raw.Length + fragment.Length > _turn.Plan.Options.Limits.MaxEnvelopeCharacters)
        {
            Reject(_turn.Plan.Error(
                ToolEnvelopeErrorCode.OutputTooLarge,
                $"Expected at most {_turn.Plan.Options.Limits.MaxEnvelopeCharacters} accumulated "
                + $"response characters, but this fragment would raise the total from {_raw.Length} "
                + $"to {_raw.Length + fragment.Length}.",
                string.Empty,
                PreviewRaw(fragment)));
            throw new ToolEnvelopeException(_terminalError!);
        }

        _raw.Append(fragment);
        if (_projection == ProjectionKind.Unknown)
        {
            var detection = DetectProjection(_raw);
            _projection = detection.Kind;
            _stringIndex = detection.ValueStart;
            if (_projection == ProjectionKind.ToolCalls)
            {
                _toolProjection = new CanonicalToolProjection();
                return _toolProjection.Feed(_raw.ToString());
            }
        }

        return _projection switch
        {
            ProjectionKind.Text => FeedString(isRefusal: false),
            ProjectionKind.Refusal => FeedString(isRefusal: true),
            ProjectionKind.ToolCalls => _toolProjection!.Feed(fragment),
            _ => Array.Empty<ToolEnvelopeStreamUpdate>(),
        };
    }

    /// <summary>Completes, parses, and validates the accumulated model response.</summary>
    public ToolEnvelopeOutcome Complete()
    {
        if (TryComplete(out var outcome, out var error))
            return outcome;
        throw new ToolEnvelopeException(error!);
    }

    /// <summary>Attempts to complete, parse, and validate the accumulated response.</summary>
    public bool TryComplete(
        [NotNullWhen(true)] out ToolEnvelopeOutcome? outcome,
        [NotNullWhen(false)] out ToolEnvelopeError? error)
    {
        if (_completionInvoked)
        {
            throw new InvalidOperationException(
                "Complete or TryComplete has already been called for this single-use stream reader. "
                + "Retain the first returned outcome or error; create a new reader from the same "
                + "ToolEnvelopeTurn for a separate model attempt.");
        }
        _completionInvoked = true;
        if (_state == ReaderState.Rejected)
        {
            outcome = null;
            error = _terminalError!;
            return false;
        }

        var raw = _raw.ToString();
        if (_turn.TryParse(raw, out outcome, out error))
        {
            _state = ReaderState.Completed;
            return true;
        }

        Reject(error!);
        return false;
    }

    private IReadOnlyList<ToolEnvelopeStreamUpdate> FeedString(bool isRefusal)
    {
        if (_stringIndex < 0 || _stringIndex >= _raw.Length)
            return Array.Empty<ToolEnvelopeStreamUpdate>();

        var decoded = new StringBuilder();
        while (_stringIndex < _raw.Length)
        {
            var character = _raw[_stringIndex++];
            if (_unicodeDigits > 0)
            {
                if (!TryHex(character, out var value))
                {
                    RejectMalformedString("A Unicode escape contains a non-hexadecimal character.");
                    throw new ToolEnvelopeException(_terminalError!);
                }

                _unicodeValue = (_unicodeValue << 4) | value;
                _unicodeDigits--;
                if (_unicodeDigits == 0)
                    AppendUnicode(decoded, (char)_unicodeValue);
                continue;
            }

            if (_stringEscaped)
            {
                _stringEscaped = false;
                switch (character)
                {
                    case '"': decoded.Append('"'); break;
                    case '\\': decoded.Append('\\'); break;
                    case '/': decoded.Append('/'); break;
                    case 'b': decoded.Append('\b'); break;
                    case 'f': decoded.Append('\f'); break;
                    case 'n': decoded.Append('\n'); break;
                    case 'r': decoded.Append('\r'); break;
                    case 't': decoded.Append('\t'); break;
                    case 'u':
                        _unicodeDigits = 4;
                        _unicodeValue = 0;
                        break;
                    default:
                        RejectMalformedString($"Unknown JSON escape '\\{character}'.");
                        throw new ToolEnvelopeException(_terminalError!);
                }

                continue;
            }

            if (character == '\\')
            {
                _stringEscaped = true;
                continue;
            }

            if (character == '"')
            {
                if (_pendingHighSurrogate is not null)
                {
                    RejectMalformedString("A Unicode high surrogate is missing its low surrogate.");
                    throw new ToolEnvelopeException(_terminalError!);
                }

                _projection = ProjectionKind.Finished;
                break;
            }

            if (character < 0x20)
            {
                RejectMalformedString("A JSON string contains an unescaped control character.");
                throw new ToolEnvelopeException(_terminalError!);
            }

            if (_pendingHighSurrogate is not null || char.IsSurrogate(character))
                AppendUnicode(decoded, character);
            else
                decoded.Append(character);
        }

        if (decoded.Length == 0)
            return Array.Empty<ToolEnvelopeStreamUpdate>();

        return isRefusal
            ? new ToolEnvelopeStreamUpdate[]
            {
                new ToolEnvelopeStreamUpdate.RefusalDelta(decoded.ToString()),
            }
            : new ToolEnvelopeStreamUpdate[]
            {
                new ToolEnvelopeStreamUpdate.AssistantTextDelta(decoded.ToString()),
            };
    }

    private void AppendUnicode(StringBuilder output, char character)
    {
        if (char.IsHighSurrogate(character))
        {
            if (_pendingHighSurrogate is not null)
            {
                RejectMalformedString("Two Unicode high surrogates occur without a low surrogate.");
                throw new ToolEnvelopeException(_terminalError!);
            }

            _pendingHighSurrogate = character;
            return;
        }

        if (char.IsLowSurrogate(character))
        {
            if (_pendingHighSurrogate is null)
            {
                RejectMalformedString("A Unicode low surrogate occurs without a high surrogate.");
                throw new ToolEnvelopeException(_terminalError!);
            }

            output.Append(_pendingHighSurrogate.Value).Append(character);
            _pendingHighSurrogate = null;
            return;
        }

        if (_pendingHighSurrogate is not null)
        {
            RejectMalformedString("A Unicode high surrogate is missing its low surrogate.");
            throw new ToolEnvelopeException(_terminalError!);
        }

        output.Append(character);
    }

    private void RejectMalformedString(string message) =>
        Reject(_turn.Plan.Error(
            ToolEnvelopeErrorCode.MalformedJson,
            message,
            _projection == ProjectionKind.Text ? "/text" : "/refusal",
            PreviewRaw()));

    private string PreviewRaw(string? suffix = null)
    {
        var maximum = _turn.Plan.Options.Limits.MaxDiagnosticPreviewCharacters;
        var preview = new StringBuilder(Math.Min(maximum, _raw.Length + (suffix?.Length ?? 0)));
        for (var index = 0; index < _raw.Length && preview.Length < maximum; index++)
            preview.Append(_raw[index]);
        if (suffix is not null)
        {
            for (var index = 0; index < suffix.Length && preview.Length < maximum; index++)
                preview.Append(suffix[index]);
        }

        return preview.ToString();
    }

    private void Reject(ToolEnvelopeError error)
    {
        _state = ReaderState.Rejected;
        _terminalError = error;
    }

    private void EnsureOpen()
    {
        if (_state != ReaderState.Open)
        {
            throw new InvalidOperationException(
                _state == ReaderState.Completed
                    ? "This single-use stream reader already completed successfully, so it cannot "
                      + "accept another model fragment. Create a new reader for a separate attempt."
                    : "This single-use stream reader already rejected the response, so it cannot "
                      + "accept another model fragment. Inspect the original ToolEnvelopeError and "
                      + "retry through a new turn or reader with bounded backpressure.");
        }
    }

    private static ProjectionDetection DetectProjection(StringBuilder raw)
    {
        var index = 0;
        SkipWhitespace(raw, ref index);
        if (index >= raw.Length)
            return ProjectionDetection.Incomplete;
        if (raw[index++] != '{')
            return ProjectionDetection.Unsupported;
        SkipWhitespace(raw, ref index);
        if (!TryReadSimpleString(raw, ref index, out var property, out var complete) || !complete)
            return complete ? ProjectionDetection.Unsupported : ProjectionDetection.Incomplete;
        SkipWhitespace(raw, ref index);
        if (index >= raw.Length)
            return ProjectionDetection.Incomplete;
        if (raw[index++] != ':')
            return ProjectionDetection.Unsupported;
        SkipWhitespace(raw, ref index);

        if (property == "tool_calls")
            return new ProjectionDetection(ProjectionKind.ToolCalls, -1);
        if (property is not ("text" or "refusal"))
            return ProjectionDetection.Unsupported;
        if (index >= raw.Length)
            return ProjectionDetection.Incomplete;
        if (raw[index] != '"')
            return ProjectionDetection.Unsupported;

        return new ProjectionDetection(
            property == "text" ? ProjectionKind.Text : ProjectionKind.Refusal,
            index + 1);
    }

    private static bool TryReadSimpleString(
        StringBuilder raw,
        ref int index,
        out string value,
        out bool complete)
    {
        value = string.Empty;
        complete = false;
        if (index >= raw.Length)
            return false;
        if (raw[index++] != '"')
        {
            complete = true;
            return false;
        }

        var text = new StringBuilder();
        while (index < raw.Length)
        {
            var character = raw[index++];
            if (character == '"')
            {
                value = text.ToString();
                complete = true;
                return true;
            }

            if (character == '\\' || character < 0x20)
            {
                complete = true;
                return false;
            }

            text.Append(character);
        }

        return false;
    }

    private static void SkipWhitespace(StringBuilder raw, ref int index)
    {
        while (index < raw.Length && raw[index] is ' ' or '\t' or '\r' or '\n')
            index++;
    }

    private static bool TryHex(char character, out int value)
    {
        value = character switch
        {
            >= '0' and <= '9' => character - '0',
            >= 'a' and <= 'f' => character - 'a' + 10,
            >= 'A' and <= 'F' => character - 'A' + 10,
            _ => -1,
        };
        return value >= 0;
    }

    private enum ProjectionKind
    {
        Unknown,
        Text,
        Refusal,
        ToolCalls,
        Unsupported,
        Finished,
    }

    private enum ReaderState
    {
        Open,
        Completed,
        Rejected,
    }

    private readonly record struct ProjectionDetection(ProjectionKind Kind, int ValueStart)
    {
        internal static ProjectionDetection Incomplete { get; } = new(ProjectionKind.Unknown, -1);
        internal static ProjectionDetection Unsupported { get; } = new(ProjectionKind.Unsupported, -1);
    }

}

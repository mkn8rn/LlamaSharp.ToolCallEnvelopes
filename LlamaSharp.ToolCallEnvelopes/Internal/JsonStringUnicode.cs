using System.Text.Json;

namespace LlamaSharp.ToolCallEnvelopes.Internal;

internal static class JsonStringUnicode
{
    internal static bool TryValidate(JsonElement value, out string? problem)
    {
        var raw = value.GetRawText();
        var lastContentIndex = raw.Length - 1;
        for (var index = 1; index < lastContentIndex; index++)
        {
            var character = raw[index];
            if (character == '\\')
            {
                index++;
                if (raw[index] != 'u')
                    continue;

                var escaped = ReadHexCodeUnit(raw, index + 1);
                index += 4;
                if (char.IsLowSurrogate((char)escaped))
                {
                    problem =
                        $"The JSON string contains escaped low surrogate U+{escaped:X4} without "
                        + "an immediately preceding escaped high surrogate.";
                    return false;
                }

                if (!char.IsHighSurrogate((char)escaped))
                    continue;

                if (index + 6 >= lastContentIndex
                    || raw[index + 1] != '\\'
                    || raw[index + 2] != 'u')
                {
                    problem =
                        $"The JSON string contains escaped high surrogate U+{escaped:X4} without "
                        + "an immediately following escaped low surrogate.";
                    return false;
                }

                var low = ReadHexCodeUnit(raw, index + 3);
                if (!char.IsLowSurrogate((char)low))
                {
                    problem =
                        $"The JSON string contains escaped high surrogate U+{escaped:X4} followed "
                        + $"by U+{low:X4}, which is not a low surrogate.";
                    return false;
                }

                index += 6;
                continue;
            }

            if (char.IsLowSurrogate(character))
            {
                problem =
                    $"The JSON string contains raw low surrogate U+{(int)character:X4} without an "
                    + "immediately preceding raw high surrogate.";
                return false;
            }

            if (!char.IsHighSurrogate(character))
                continue;

            if (index + 1 >= lastContentIndex || !char.IsLowSurrogate(raw[index + 1]))
            {
                problem =
                    $"The JSON string contains raw high surrogate U+{(int)character:X4} without an "
                    + "immediately following raw low surrogate.";
                return false;
            }

            index++;
        }

        problem = null;
        return true;
    }

    private static int ReadHexCodeUnit(string raw, int start)
    {
        var value = 0;
        for (var index = start; index < start + 4; index++)
        {
            value = (value << 4) | raw[index] switch
            {
                >= '0' and <= '9' => raw[index] - '0',
                >= 'a' and <= 'f' => raw[index] - 'a' + 10,
                >= 'A' and <= 'F' => raw[index] - 'A' + 10,
                _ => throw new InvalidOperationException(
                    "A parsed JSON string contained a non-hexadecimal Unicode escape. This "
                    + "indicates a System.Text.Json or package defect because syntax validation "
                    + "must finish before Unicode scalar validation. Reject the response and "
                    + "report the exact model output to the package maintainer."),
            };
        }

        return value;
    }
}

using FluentAssertions;

namespace LlamaSharp.ToolCallEnvelopes.Tests;

[TestFixture]
public sealed class LlamaSharpToolEnvelopeStreamParserTests
{
    [Test]
    public void Feed_MessageMode_StreamsDecodedTextOnly()
    {
        var parser = new LlamaSharpToolEnvelopeStreamParser();
        var chunks = FeedCharacters(parser, """{"mode":"message","text":"Hello\nworld","calls":[]}""");

        string.Concat(chunks.Where(c => c.TextDelta is not null).Select(c => c.TextDelta))
            .Should().Be("Hello\nworld");
        chunks.Should().NotContain(c => c.ToolCallDelta != null);
        parser.Complete().Content.Should().Be("Hello\nworld");
    }

    [Test]
    public void Feed_RefusalMode_StreamsDecodedRefusalText()
    {
        var parser = new LlamaSharpToolEnvelopeStreamParser();
        var chunks = FeedCharacters(parser, """{"mode":"refusal","text":"No thanks","calls":[]}""");

        string.Concat(chunks.Where(c => c.TextDelta is not null).Select(c => c.TextDelta))
            .Should().Be("No thanks");
        parser.Complete().Refusal.Should().Be("No thanks");
    }

    [Test]
    public void Feed_ToolCallsMode_EmitsIdNameAndCompleteArgs()
    {
        var parser = new LlamaSharpToolEnvelopeStreamParser();
        var chunks = FeedCharacters(
            parser,
            """{"mode":"tool_calls","text":"","calls":[{"id":"call_a","name":"get_weather","args":{"city":"Paris"}}]}""");

        var deltas = chunks
            .Where(c => c.ToolCallDelta is not null)
            .Select(c => c.ToolCallDelta!)
            .ToArray();

        deltas.Should().Contain(d => d.Index == 0 && d.Id == "call_a");
        deltas.Should().Contain(d => d.Index == 0 && d.Name == "get_weather");
        ConcatArgs(deltas, 0).Should().Be("""{"city":"Paris"}""");
        parser.Complete().ToolCalls.Should().ContainSingle()
            .Which.Name.Should().Be("get_weather");
    }

    [Test]
    public void Feed_ToolCallsMode_MultipleCalls_TracksIndexes()
    {
        var parser = new LlamaSharpToolEnvelopeStreamParser();
        var chunks = FeedCharacters(
            parser,
            """{"mode":"tool_calls","text":"","calls":[{"id":"c1","name":"a","args":{"x":1}},{"id":"c2","name":"b","args":{"y":2}}]}""");

        var deltas = chunks
            .Where(c => c.ToolCallDelta is not null)
            .Select(c => c.ToolCallDelta!)
            .ToArray();

        deltas.Should().Contain(d => d.Index == 0 && d.Id == "c1");
        deltas.Should().Contain(d => d.Index == 1 && d.Id == "c2");
        deltas.Should().Contain(d => d.Index == 0 && d.Name == "a");
        deltas.Should().Contain(d => d.Index == 1 && d.Name == "b");
        ConcatArgs(deltas, 0).Should().Be("""{"x":1}""");
        ConcatArgs(deltas, 1).Should().Be("""{"y":2}""");
    }

    [Test]
    public void Feed_ToolCallsMode_NestedArgsAndBracesInsideStrings_DoNotTerminateEarly()
    {
        var parser = new LlamaSharpToolEnvelopeStreamParser();
        var chunks = FeedCharacters(
            parser,
            """{"mode":"tool_calls","text":"","calls":[{"id":"c","name":"n","args":{"a":{"b":[1,2]},"s":"x}y"}}]}""");

        var deltas = chunks
            .Where(c => c.ToolCallDelta is not null)
            .Select(c => c.ToolCallDelta!)
            .ToArray();

        ConcatArgs(deltas, 0).Should().Be("""{"a":{"b":[1,2]},"s":"x}y"}""");
    }

    [Test]
    public void Feed_ToolCallsMode_UnicodeEscapesInNameAreDecoded()
    {
        var parser = new LlamaSharpToolEnvelopeStreamParser();
        var chunks = FeedCharacters(
            parser,
            """{"mode":"tool_calls","text":"","calls":[{"id":"c","name":"caf\u00e9","args":{}}]}""");

        var name = chunks
            .Select(c => c.ToolCallDelta)
            .Where(d => d?.Name is not null)
            .Select(d => d!.Name)
            .Single();

        name.Should().Be("cafe\u0301".Normalize());
    }

    [Test]
    public void Complete_MalformedEnvelope_ThrowsAfterStreaming()
    {
        var parser = new LlamaSharpToolEnvelopeStreamParser(
            new ToolEnvelopeParserOptions { EnvelopeMode = ToolEnvelopeMode.StrictDeclared });
        parser.Feed("""{"mode":"message","text":"Hello","calls":[{"id":"c","name":"n","args":{}}]}""");

        var act = () => parser.Complete();

        act.Should().Throw<LlamaSharpToolEnvelopeException>()
            .WithMessage("*Message envelopes must not contain tool calls*");
    }

    [Test]
    public void Feed_InferredMessageWithoutMode_StreamsText()
    {
        var parser = new LlamaSharpToolEnvelopeStreamParser();
        var chunks = FeedCharacters(parser, """{"text":"Hallo"}""");

        string.Concat(chunks.Where(c => c.TextDelta is not null).Select(c => c.TextDelta))
            .Should().Be("Hallo");
        parser.Complete().Kind.Should().Be(ToolEnvelopeResultMode.Message);
    }

    [Test]
    public void Feed_InferredToolCallsWithArgumentsProperty_EmitsDeltas()
    {
        var parser = new LlamaSharpToolEnvelopeStreamParser();
        var chunks = FeedCharacters(
            parser,
            """{"tool_calls":[{"id":"c","name":"lookup","arguments":{"q":"x"}}]}""");

        var deltas = chunks
            .Where(c => c.ToolCallDelta is not null)
            .Select(c => c.ToolCallDelta!)
            .ToArray();

        deltas.Should().Contain(d => d.Id == "c");
        deltas.Should().Contain(d => d.Name == "lookup");
        ConcatArgs(deltas, 0).Should().Be("{\"q\":\"x\"}");
    }

    [Test]
    public void Feed_InferredLegacyMessageWithCalls_SwitchesToCallDeltas()
    {
        var parser = new LlamaSharpToolEnvelopeStreamParser();
        var chunks = FeedCharacters(
            parser,
            """{"mode":"message","text":"ignored","calls":[{"id":"c","name":"lookup","args":{}}]}""");

        chunks.Select(chunk => chunk.ToolCallDelta)
            .Where(delta => delta is not null)
            .Should().Contain(delta => delta!.Name == "lookup");
        parser.Complete().Kind.Should().Be(ToolEnvelopeResultMode.ToolCalls);
    }

    [Test]
    public void StrictStreamValidation_RejectsMessageModeWhenCallStarts()
    {
        var parser = new LlamaSharpToolEnvelopeStreamParser(
            new ToolEnvelopeParserOptions { EnvelopeMode = ToolEnvelopeMode.StrictDeclared },
            ToolEnvelopeStreamValidation.Strict);

        var act = () => parser.Feed(
            """{"mode":"message","text":"x","calls":[{"id":"c"}""");

        act.Should().Throw<LlamaSharpToolEnvelopeException>()
            .Which.Code.Should().Be("EnvelopeModePayloadMismatch");
    }

    private static List<ToolEnvelopeStreamChunk> FeedCharacters(
        LlamaSharpToolEnvelopeStreamParser parser,
        string envelope)
    {
        var chunks = new List<ToolEnvelopeStreamChunk>();
        foreach (var ch in envelope)
            chunks.AddRange(parser.Feed(ch.ToString()));
        return chunks;
    }

    private static string ConcatArgs(IEnumerable<ToolCallDelta> deltas, int index) =>
        string.Concat(deltas
            .Where(d => d.Index == index && d.ArgumentsFragment is not null)
            .Select(d => d.ArgumentsFragment));
}

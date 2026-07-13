namespace LlamaSharp.ToolCallEnvelopes.Tests;

[TestFixture]
public sealed class ToolEnvelopeStreamReaderTests
{
    [Test]
    public void TextStreaming_DecodesEveryEscapeAndSurrogateExactlyOnceAcrossAllBoundaries()
    {
        const string raw = "{\"text\":\"A\\n\\t\\\"\\\\\\/\\b\\f\\r\\uD83D\\uDE00Z\"}";
        const string expected = "A\n\t\"\\/\b\f\r😀Z";
        var turn = TestCatalog.Turn();

        for (var split = 0; split <= raw.Length; split++)
        {
            var reader = turn.CreateStreamReader();
            var updates = reader.Feed(raw[..split])
                .Concat(reader.Feed(raw[split..]))
                .OfType<ToolEnvelopeStreamUpdate.AssistantTextDelta>()
                .Select(update => update.Text);

            string.Concat(updates).Should().Be(expected, $"split {split}");
            reader.Complete().Should().BeOfType<ToolEnvelopeOutcome.AssistantMessage>();
        }
    }

    [Test]
    public void TextStreaming_CoalescesOneLargeInputFragmentIntoOneDelta()
    {
        var limits = ToolEnvelopeLimits.Constrained with { MaxFinalTextCharacters = 25_000 };
        var reader = TestCatalog.Turn(limits: limits).CreateStreamReader();
        var text = new string('x', 20_000);

        var updates = reader.Feed($"{{\"text\":\"{text}\"}}");

        updates.Should().ContainSingle()
            .Which.Should().BeOfType<ToolEnvelopeStreamUpdate.AssistantTextDelta>()
            .Which.Text.Should().Be(text);
        reader.Complete().Should().BeOfType<ToolEnvelopeOutcome.AssistantMessage>();
    }

    [Test]
    public void ToolStreaming_CoalescesArgumentsPerCallAndPerFragment()
    {
        var turn = TestCatalog.Turn(maxCalls: 2);
        const string raw =
            """
            {"tool_calls":[{"name":"get_weather","arguments":{"city":"Zagreb","unit":"celsius"}},{"name":"get_weather","arguments":{"city":"Split","unit":"fahrenheit"}}]}
            """;
        var reader = turn.CreateStreamReader();

        var updates = reader.Feed(raw)
            .OfType<ToolEnvelopeStreamUpdate.ToolArgumentsDelta>()
            .ToArray();

        updates.Should().HaveCount(2);
        updates.Select(update => update.CallIndex).Should().Equal(0, 1);
        updates[0].Json.Should().Be("""{"city":"Zagreb","unit":"celsius"}""");
        updates[1].Json.Should().Be("""{"city":"Split","unit":"fahrenheit"}""");
        reader.Complete().Should().BeOfType<ToolEnvelopeOutcome.ToolRequest>();
    }

    [Test]
    public void ToolStreaming_ReconstructsArgumentsWhenFedOneCharacterAtATime()
    {
        var turn = TestCatalog.Turn(ToolChoice.Required);
        var raw = TestCatalog.ToolRequest(
            "get_weather",
            """{"city":"Zagreb","unit":"celsius","tags":["a","b"]}""");
        var reader = turn.CreateStreamReader();
        var fragments = new List<string>();

        foreach (var character in raw)
        {
            fragments.AddRange(reader.Feed(character.ToString())
                .OfType<ToolEnvelopeStreamUpdate.ToolArgumentsDelta>()
                .Select(update => update.Json));
        }

        string.Concat(fragments).Should().Be(
            """{"city":"Zagreb","unit":"celsius","tags":["a","b"]}""");
        reader.Complete().Should().BeOfType<ToolEnvelopeOutcome.ToolRequest>();
    }

    [Test]
    public void NonCanonicalPropertyOrder_StillCompletesWithoutPublishingMisclassifiedArguments()
    {
        var turn = TestCatalog.Turn(ToolChoice.Required);
        const string raw =
            """
            {"tool_calls":[{"arguments":{"city":"Zagreb","unit":"celsius"},"name":"get_weather"}]}
            """;
        var reader = turn.CreateStreamReader();

        reader.Feed(raw).Should().BeEmpty();
        reader.Complete().Should().BeOfType<ToolEnvelopeOutcome.ToolRequest>();
    }

    [Test]
    public void RefusalStreaming_IsProvisionalAndDecoded()
    {
        var reader = TestCatalog.Turn(
            ToolChoice.None,
            allowRefusal: true).CreateStreamReader();

        var updates = reader.Feed("""{"refusal":"Not available."}""");

        updates.Should().ContainSingle()
            .Which.Should().BeOfType<ToolEnvelopeStreamUpdate.RefusalDelta>()
            .Which.Text.Should().Be("Not available.");
        reader.Complete().Should().BeOfType<ToolEnvelopeOutcome.Refusal>();
    }

    [Test]
    public void ProvisionalText_IsRejectedTransactionallyWhenFinalShapeIsInvalid()
    {
        var reader = TestCatalog.Turn().CreateStreamReader();

        reader.Feed("""{"text":"optimistic","extra":true}""")
            .Should().ContainSingle()
            .Which.Should().BeOfType<ToolEnvelopeStreamUpdate.AssistantTextDelta>();
        reader.TryComplete(out var outcome, out var error).Should().BeFalse();

        outcome.Should().BeNull();
        error!.Code.Should().Be(ToolEnvelopeErrorCode.UnknownProperty);
        Action feedAgain = () => reader.Feed("more");
        Action completeAgain = () => reader.TryComplete(out _, out _);
        feedAgain.Should().Throw<InvalidOperationException>();
        completeAgain.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void Feed_RejectsOversizeAndMalformedStringDataEarly()
    {
        var smallLimits = ToolEnvelopeLimits.Constrained with { MaxEnvelopeCharacters = 20 };
        var oversized = TestCatalog.Turn(limits: smallLimits).CreateStreamReader();
        var malformed = TestCatalog.Turn().CreateStreamReader();

        Action exceedLimit = () => oversized.Feed(new string('x', 21));
        Action feedMalformedEscape = () => malformed.Feed("{\"text\":\"\\uZZZZ");
        var sizeException = exceedLimit.Should().Throw<ToolEnvelopeException>().Which;
        var escapeException = feedMalformedEscape.Should().Throw<ToolEnvelopeException>().Which;

        sizeException.Error.Code.Should().Be(ToolEnvelopeErrorCode.OutputTooLarge);
        sizeException.Error.PayloadPreview.Should().Be(new string('x', 21));
        escapeException.Error.Code.Should().Be(ToolEnvelopeErrorCode.MalformedJson);
        oversized.TryComplete(out _, out var sizeError).Should().BeFalse();
        sizeError.Should().Be(sizeException.Error);
        malformed.TryComplete(out _, out var malformedError).Should().BeFalse();
        malformedError.Should().Be(escapeException.Error);
    }

    [Test]
    public void TextStreaming_ValidatesRawSurrogatePairsBeforePublishingThem()
    {
        var valid = TestCatalog.Turn().CreateStreamReader();
        var invalid = TestCatalog.Turn().CreateStreamReader();
        var pair = "\ud83d\ude00";
        var unpaired = "\ud800";

        var updates = valid.Feed("{\"text\":\"" + pair + "\"}");
        Action feedUnpaired = () => invalid.Feed("{\"text\":\"" + unpaired + "\"}");

        updates.Should().ContainSingle()
            .Which.Should().BeOfType<ToolEnvelopeStreamUpdate.AssistantTextDelta>()
            .Which.Text.Should().Be(pair);
        valid.Complete().Should().BeOfType<ToolEnvelopeOutcome.AssistantMessage>();
        feedUnpaired.Should().Throw<ToolEnvelopeException>()
            .Which.Error.Code.Should().Be(ToolEnvelopeErrorCode.MalformedJson);
    }

    [TestCase("{\"text\":\"\\x\"}", "Unknown JSON escape")]
    [TestCase("{\"text\":\"\\uD800\\uD800\"}", "Two Unicode high surrogates")]
    [TestCase("{\"text\":\"\\uDC00\"}", "low surrogate occurs without")]
    [TestCase("{\"text\":\"\\uD800A\"}", "high surrogate is missing")]
    public void TextStreaming_RejectsEveryMalformedEscapeAndSurrogateSequenceEarly(
        string raw,
        string expectedProblem)
    {
        var reader = TestCatalog.Turn().CreateStreamReader();

        Action feed = () => reader.Feed(raw);

        var exception = feed.Should().Throw<ToolEnvelopeException>().Which;
        exception.Error.Code.Should().Be(ToolEnvelopeErrorCode.MalformedJson);
        exception.Error.JsonPointer.Should().Be("/text");
        exception.Error.Message.Should().Contain(expectedProblem);
    }

    [TestCase(" ", ToolEnvelopeErrorCode.EmptyOutput)]
    [TestCase("[]", ToolEnvelopeErrorCode.ExpectedObject)]
    [TestCase("{1", ToolEnvelopeErrorCode.MalformedJson)]
    [TestCase("{\"te", ToolEnvelopeErrorCode.MalformedJson)]
    [TestCase("{\"te\\", ToolEnvelopeErrorCode.MalformedJson)]
    [TestCase("{\"text\"", ToolEnvelopeErrorCode.MalformedJson)]
    [TestCase("{\"text\" 1", ToolEnvelopeErrorCode.MalformedJson)]
    [TestCase("{\"other\":\"x\"}", ToolEnvelopeErrorCode.UnknownProperty)]
    [TestCase("{\"text\":1}", ToolEnvelopeErrorCode.InvalidPropertyType)]
    public void ConservativeProjectionDetection_DefersUnsupportedPrefixesToFinalValidation(
        string raw,
        ToolEnvelopeErrorCode expectedCode)
    {
        var reader = TestCatalog.Turn().CreateStreamReader();

        reader.Feed(raw).Should().BeEmpty();
        reader.TryComplete(out var outcome, out var error).Should().BeFalse();

        outcome.Should().BeNull();
        error!.Code.Should().Be(expectedCode);
    }

    [Test]
    public void EarlyDiagnostics_BoundOnlyThePayloadAndKeepTheRecoveryActionable()
    {
        var limits = ToolEnvelopeLimits.Constrained with
        {
            MaxDiagnosticPreviewCharacters = 16,
        };
        var reader = TestCatalog.Turn(limits: limits).CreateStreamReader();

        Action feedControl = () => reader.Feed("{\"text\":\"\0");

        var exception = feedControl.Should().Throw<ToolEnvelopeException>().Which;
        exception.Error.PayloadPreview.Should().NotContain("\0");
        exception.Error.PayloadPreview.Should().Contain("\\u0000");
        exception.Error.PayloadPreview.Length.Should().BeLessThanOrEqualTo(16);
        exception.Error.Message.Should().Contain("MalformedJson")
            .And.Contain("Recovery:")
            .And.Contain("bounded backpressure");
        exception.Error.JsonPointer.Should().Be("/text");
    }

    [Test]
    public void ReaderLifecycle_IsSingleUseAndEmptyFragmentsAreNoOps()
    {
        var reader = TestCatalog.Turn().CreateStreamReader();

        reader.Feed(string.Empty).Should().BeEmpty();
        reader.Feed("""{"text":"done"}""");
        reader.Complete().Should().BeOfType<ToolEnvelopeOutcome.AssistantMessage>();

        Action feedAgain = () => reader.Feed("again");
        Action completeAgain = () => reader.Complete();
        feedAgain.Should().Throw<InvalidOperationException>();
        completeAgain.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void StreamingAllocation_RemainsBoundedForSixtyFourCharacterFragments()
    {
        var limits = ToolEnvelopeLimits.Constrained with { MaxFinalTextCharacters = 25_000 };
        var turn = TestCatalog.Turn(limits: limits);
        var raw = $"{{\"text\":\"{new string('x', 20_000)}\"}}";
        var warmup = turn.CreateStreamReader();
        warmup.Feed("""{"text":"warm"}""");
        warmup.Complete();
        var reader = turn.CreateStreamReader();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var offset = 0; offset < raw.Length; offset += 64)
        {
            reader.Feed(raw.Substring(offset, Math.Min(64, raw.Length - offset)));
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.Should().BeLessThan(raw.Length * 32L);
        reader.Complete().Should().BeOfType<ToolEnvelopeOutcome.AssistantMessage>();
    }
}

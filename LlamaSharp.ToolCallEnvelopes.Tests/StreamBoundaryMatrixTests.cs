using System.Text;

namespace LlamaSharp.ToolCallEnvelopes.Tests;

[TestFixture]
public sealed class StreamBoundaryMatrixTests
{
    [Test]
    public void TextProjection_ReconstructsExactlyAcrossEveryThreeFragmentSplit()
    {
        const string raw = "{\"text\":\"A\\n\\t\\\"\\\\\\/\\b\\f\\r\\uD83D\\uDE00Z\"}";
        const string expected = "A\n\t\"\\/\b\f\r😀Z";
        var turn = TestCatalog.Turn();

        ForEveryThreeFragmentSplit(raw, (first, second, third, label) =>
        {
            var reader = turn.CreateStreamReader();
            var text = string.Concat(
                Feed(reader, first, second, third)
                    .OfType<ToolEnvelopeStreamUpdate.AssistantTextDelta>()
                    .Select(update => update.Text));

            Assert.That(text, Is.EqualTo(expected), label);
            var outcome = reader.Complete();
            Assert.That(
                outcome,
                Is.TypeOf<ToolEnvelopeOutcome.AssistantMessage>(),
                label);
            Assert.That(
                ((ToolEnvelopeOutcome.AssistantMessage)outcome).Text,
                Is.EqualTo(expected),
                label);
        });
    }

    [Test]
    public void RefusalProjection_ReconstructsExactlyAcrossEveryThreeFragmentSplit()
    {
        const string raw = "{\"refusal\":\"No\\n\\uD83D\\uDE00.\"}";
        const string expected = "No\n😀.";
        var turn = TestCatalog.Turn(ToolChoice.None, allowRefusal: true);

        ForEveryThreeFragmentSplit(raw, (first, second, third, label) =>
        {
            var reader = turn.CreateStreamReader();
            var text = string.Concat(
                Feed(reader, first, second, third)
                    .OfType<ToolEnvelopeStreamUpdate.RefusalDelta>()
                    .Select(update => update.Text));

            Assert.That(text, Is.EqualTo(expected), label);
            var outcome = reader.Complete();
            Assert.That(outcome, Is.TypeOf<ToolEnvelopeOutcome.Refusal>(), label);
            Assert.That(
                ((ToolEnvelopeOutcome.Refusal)outcome).Reason,
                Is.EqualTo(expected),
                label);
        });
    }

    [Test]
    public void MultiCallProjection_ReconstructsNestedArgumentsAcrossEveryThreeFragmentSplit()
    {
        const string firstArguments =
            """{"city":"A } [ \\\"","unit":"celsius","tags":["x}","y["],"coordinates":{"latitude":45.8,"longitude":16}}""";
        const string secondArguments =
            """{"city":"Split","unit":"fahrenheit","include_alerts":true}""";
        var raw =
            $$"""{"tool_calls":[{"name":"get_weather","arguments":{{firstArguments}}},{"name":"get_weather","arguments":{{secondArguments}}}]}""";
        var turn = TestCatalog.Turn(ToolChoice.Required, maxCalls: 2);

        ForEveryThreeFragmentSplit(raw, (first, second, third, label) =>
        {
            var reader = turn.CreateStreamReader();
            var reconstructed = new[] { new StringBuilder(), new StringBuilder() };
            foreach (var update in Feed(reader, first, second, third)
                         .OfType<ToolEnvelopeStreamUpdate.ToolArgumentsDelta>())
            {
                Assert.That(update.CallIndex, Is.InRange(0, 1), label);
                reconstructed[update.CallIndex].Append(update.Json);
            }

            Assert.That(reconstructed[0].ToString(), Is.EqualTo(firstArguments), label);
            Assert.That(reconstructed[1].ToString(), Is.EqualTo(secondArguments), label);
            var outcome = reader.Complete();
            Assert.That(outcome, Is.TypeOf<ToolEnvelopeOutcome.ToolRequest>(), label);
            Assert.That(
                ((ToolEnvelopeOutcome.ToolRequest)outcome).Calls.Count,
                Is.EqualTo(2),
                label);
        });
    }

    public static IEnumerable<TestCaseData> ValidNonCanonicalCalls
    {
        get
        {
            yield return NonCanonical(
                "arguments before name",
                """{"tool_calls":[{"arguments":{"city":"Zagreb","unit":"celsius"},"name":"get_weather"}]}""");
            yield return NonCanonical(
                "escaped name property",
                """{"tool_calls":[{"\u006eame":"get_weather","arguments":{"city":"Zagreb","unit":"celsius"}}]}""");
            yield return NonCanonical(
                "escaped arguments property",
                """{"tool_calls":[{"name":"get_weather","arg\u0075ments":{"city":"Zagreb","unit":"celsius"}}]}""");
            yield return NonCanonical(
                "escaped tool name",
                """{"tool_calls":[{"name":"get_\u0077eather","arguments":{"city":"Zagreb","unit":"celsius"}}]}""");
        }
    }

    [TestCaseSource(nameof(ValidNonCanonicalCalls))]
    public void ValidNonCanonicalCall_EmitsNoMisleadingProjectionButStillCompletes(string raw)
    {
        var reader = TestCatalog.Turn(ToolChoice.Required).CreateStreamReader();

        reader.Feed(raw).Should().BeEmpty();

        var outcome = reader.Complete()
            .Should().BeOfType<ToolEnvelopeOutcome.ToolRequest>().Which;
        outcome.Calls.Should().ContainSingle();
        outcome.Calls[0].Name.Should().Be("get_weather");
    }

    [Test]
    public void ProvisionalToolArguments_AreRejectedTransactionallyWhenFinalShapeIsInvalid()
    {
        const string raw =
            """{"tool_calls":[{"name":"get_weather","arguments":{"city":"Zagreb","unit":"celsius"}}],"extra":true}""";
        var reader = TestCatalog.Turn(ToolChoice.Required).CreateStreamReader();

        var provisional = reader.Feed(raw)
            .Should().ContainSingle()
            .Which.Should().BeOfType<ToolEnvelopeStreamUpdate.ToolArgumentsDelta>()
            .Which;
        provisional.CallIndex.Should().Be(0);
        provisional.Json.Should().Be("""{"city":"Zagreb","unit":"celsius"}""");

        reader.TryComplete(out var outcome, out var error).Should().BeFalse();
        outcome.Should().BeNull();
        error!.Code.Should().Be(ToolEnvelopeErrorCode.UnknownProperty);
        error.JsonPointer.Should().BeEmpty();
    }

    private static IReadOnlyList<ToolEnvelopeStreamUpdate> Feed(
        ToolEnvelopeStreamReader reader,
        params string[] fragments) =>
        fragments.SelectMany(reader.Feed).ToArray();

    private static void ForEveryThreeFragmentSplit(
        string raw,
        Action<string, string, string, string> assertion)
    {
        for (var firstCut = 0; firstCut <= raw.Length; firstCut++)
        {
            for (var secondCut = firstCut; secondCut <= raw.Length; secondCut++)
            {
                assertion(
                    raw[..firstCut],
                    raw[firstCut..secondCut],
                    raw[secondCut..],
                    $"cuts {firstCut} and {secondCut}");
            }
        }
    }

    private static TestCaseData NonCanonical(string name, string raw) =>
        new TestCaseData(raw).SetName($"Noncanonical_{name.Replace(' ', '_')}");
}

namespace LlamaSharp.ToolCallEnvelopes.Tests;

[TestFixture]
public sealed class ManagedFailureDetailTests
{
    private const string ExceptionMessage = "detail\nwith\tcontrol-and-long-suffix";
    private const string SanitizedPreview = "detail\\nwith\\tcontro";

    public static IEnumerable<TestCaseData> EveryHostCallbackBoundary =>
        new[] { "inference", "dispatcher", "observer" }
            .Select(boundary => new TestCaseData(boundary).SetName($"Details_{boundary}"));

    [TestCaseSource(nameof(EveryHostCallbackBoundary))]
    public async Task EveryHostCallbackFailure_PreservesTypeAndBoundedSanitizedDetail(
        string boundary)
    {
        var plan = TestCatalog.Plan(
            limits: ToolEnvelopeLimits.Constrained with
            {
                MaxDiagnosticPreviewCharacters = 20,
            });
        var executor = boundary == "inference"
            ? new QueueExecutor((_, _) => throw new TestAdapterException(ExceptionMessage))
            : new QueueExecutor(boundary == "dispatcher"
                ? TestCatalog.ToolRequest(
                    "get_weather",
                    "{\"city\":\"Zagreb\",\"unit\":\"celsius\"}")
                : "{\"text\":\"done\"}");
        ToolDispatcher dispatcher = boundary == "dispatcher"
            ? (_, _) => throw new TestAdapterException(ExceptionMessage)
            : (_, _) => ValueTask.FromResult("{}");
        ToolRunObserver? observer = boundary == "observer"
            ? (_, _) => throw new TestAdapterException(ExceptionMessage)
            : null;

        var result = await ToolEnvelopeRunner.RunAsync(
            executor,
            plan,
            "Policy.",
            [ToolMessage.User("Request.")],
            dispatcher,
            new ToolRunOptions
            {
                InitialChoice = boundary == "dispatcher"
                    ? ToolChoice.Required
                    : ToolChoice.Auto,
                MaxAttemptsPerTurn = 1,
                Observer = observer,
            });

        var failure = result.Should().BeOfType<ToolRunResult.Failed>().Which.Failure;
        failure.Exception.Should().BeOfType<TestAdapterException>()
            .Which.Message.Should().Be(ExceptionMessage);
        failure.Message.Should().Contain($"TestAdapterException: {SanitizedPreview}")
            .And.NotContain("control-and-long-suffix")
            .And.NotContain("\n")
            .And.NotContain("\t");
        failure.Code.Should().Be(boundary switch
        {
            "inference" => ToolRunFailureCode.InferenceFailed,
            "dispatcher" => ToolRunFailureCode.ToolExecutionFailed,
            "observer" => ToolRunFailureCode.ObserverFailed,
            _ => throw new ArgumentOutOfRangeException(nameof(boundary)),
        });
    }
}

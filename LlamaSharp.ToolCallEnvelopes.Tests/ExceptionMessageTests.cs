namespace LlamaSharp.ToolCallEnvelopes.Tests;

[TestFixture]
public sealed class ExceptionMessageTests
{
    public static IEnumerable<TestCaseData> EveryEnvelopeError =>
        Enum.GetValues<ToolEnvelopeErrorCode>()
            .Select(code => new TestCaseData(code).SetName($"Descriptive_{code}"));

    [TestCaseSource(nameof(EveryEnvelopeError))]
    public void EveryModelResponseError_NamesTheFieldProblemAndRecovery(
        ToolEnvelopeErrorCode expectedCode)
    {
        var setup = SetupFor(expectedCode);

        setup.Turn.TryParse(setup.Output, out var outcome, out var error).Should().BeFalse();

        outcome.Should().BeNull();
        error!.Code.Should().Be(expectedCode);
        error.Message.Should().Contain("The model response is invalid at")
            .And.Contain($"({expectedCode})")
            .And.Contain("Recovery:")
            .And.MatchRegex("(?i)(retry|backpressure|change the model)");
        error.Message.Should().Contain(
            string.IsNullOrEmpty(error.JsonPointer)
                ? "the response root"
                : error.JsonPointer);
        error.JsonPointer.Should().NotBeNull();
        error.PayloadPreview.Should().NotBeNull();

        Action parse = () => setup.Turn.Parse(setup.Output);
        var exception = parse.Should().Throw<ToolEnvelopeException>().Which;
        exception.Message.Should().Be(error.Message);
        exception.Error.Should().Be(error);
    }

    [Test]
    public void PlanException_ExplainsEveryReportedProblemAndHowToRecover()
    {
        var options = new ToolEnvelopePlanOptions
        {
            MaxCallsPerTurn = 0,
            Limits = ToolEnvelopeLimits.Constrained with
            {
                MaxTools = 0,
                MaxGeneratedNumberCharacters = 3,
            },
        };

        Action compile = () => ToolEnvelopePlan.Compile([], options);

        var exception = compile.Should().Throw<ToolEnvelopePlanException>().Which;
        exception.Diagnostics.Should().HaveCount(3);
        exception.Message.Should().Contain("cannot be compiled")
            .And.Contain("Problem 1")
            .And.Contain(nameof(ToolEnvelopeLimits.MaxTools))
            .And.Contain(nameof(ToolEnvelopeLimits.MaxGeneratedNumberCharacters))
            .And.Contain(nameof(ToolEnvelopePlanOptions.MaxCallsPerTurn))
            .And.Contain("compile the plan again");
    }

    [Test]
    public void DiagnosticPreviewLimit_NeverTruncatesTheExactFieldOrRecoveryMessage()
    {
        const string propertyName = "a-very-long-unknown-field-name";
        var turn = TestCatalog.Turn(
            limits: ToolEnvelopeLimits.Constrained with
            {
                MaxDiagnosticPreviewCharacters = 8,
            });

        turn.TryParse($"{{\"{propertyName}\":true}}", out _, out var error).Should().BeFalse();

        error!.JsonPointer.Should().Be($"/{propertyName}");
        error.Message.Should().Contain($"/{propertyName}")
            .And.Contain($"Envelope property '{propertyName}' is not allowed")
            .And.Contain("Recovery:");
        error.PayloadPreview.Should().HaveLength(8);
    }

    private static ErrorSetup SetupFor(ToolEnvelopeErrorCode code) => code switch
    {
        ToolEnvelopeErrorCode.EmptyOutput => new(TestCatalog.Turn(), string.Empty),
        ToolEnvelopeErrorCode.OutputTooLarge => new(
            TestCatalog.Turn(limits: ToolEnvelopeLimits.Constrained with
            {
                MaxEnvelopeCharacters = 10,
            }),
            new string('x', 11)),
        ToolEnvelopeErrorCode.MalformedJson => new(TestCatalog.Turn(), "{"),
        ToolEnvelopeErrorCode.ExpectedObject => new(TestCatalog.Turn(), "[]"),
        ToolEnvelopeErrorCode.DuplicateProperty => new(
            TestCatalog.Turn(),
            """{"text":"first","text":"second"}"""),
        ToolEnvelopeErrorCode.UnknownProperty => new(
            TestCatalog.Turn(),
            """{"unknown":"value"}"""),
        ToolEnvelopeErrorCode.InvalidPropertyType => new(
            TestCatalog.Turn(),
            """{"text":42}"""),
        ToolEnvelopeErrorCode.EmptyText => new(
            TestCatalog.Turn(),
            """{"text":" "}"""),
        ToolEnvelopeErrorCode.TextTooLarge => new(
            TestCatalog.Turn(limits: ToolEnvelopeLimits.Constrained with
            {
                MaxFinalTextCharacters = 2,
            }),
            """{"text":"abc"}"""),
        ToolEnvelopeErrorCode.RefusalNotAllowed => new(
            TestCatalog.Turn(),
            """{"refusal":"declined"}"""),
        ToolEnvelopeErrorCode.EmptyRefusal => new(
            TestCatalog.Turn(allowRefusal: true),
            """{"refusal":" "}"""),
        ToolEnvelopeErrorCode.RefusalTooLarge => new(
            TestCatalog.Turn(
                allowRefusal: true,
                limits: ToolEnvelopeLimits.Constrained with
                {
                    MaxRefusalCharacters = 2,
                }),
            """{"refusal":"abc"}"""),
        ToolEnvelopeErrorCode.ToolCallsNotAllowed => new(
            TestCatalog.Turn(ToolChoice.None),
            ValidWeatherCall),
        ToolEnvelopeErrorCode.ToolCallsRequired => new(
            TestCatalog.Turn(ToolChoice.Required),
            """{"text":"done"}"""),
        ToolEnvelopeErrorCode.EmptyToolCalls => new(
            TestCatalog.Turn(),
            """{"tool_calls":[]}"""),
        ToolEnvelopeErrorCode.TooManyToolCalls => new(
            TestCatalog.Turn(maxCalls: 1),
            """
            {"tool_calls":[
              {"name":"get_weather","arguments":{"city":"Zagreb","unit":"celsius"}},
              {"name":"get_weather","arguments":{"city":"Split","unit":"celsius"}}
            ]}
            """),
        ToolEnvelopeErrorCode.InvalidToolCall => new(
            TestCatalog.Turn(),
            """{"tool_calls":[42]}"""),
        ToolEnvelopeErrorCode.UnknownTool => new(
            TestCatalog.Turn(),
            TestCatalog.ToolRequest("invented_tool", "{}")),
        ToolEnvelopeErrorCode.WrongTool => new(
            TestCatalog.Turn(
                ToolChoice.Named("search"),
                tools: [TestCatalog.Weather(), TestCatalog.Search()]),
            ValidWeatherCall),
        ToolEnvelopeErrorCode.InvalidArguments => new(
            TestCatalog.Turn(),
            TestCatalog.ToolRequest("get_weather", "[]")),
        ToolEnvelopeErrorCode.SchemaViolation => new(
            TestCatalog.Turn(),
            TestCatalog.ToolRequest("get_weather", "{}")),
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unmapped error code."),
    };

    private static string ValidWeatherCall => TestCatalog.ToolRequest(
        "get_weather",
        """{"city":"Zagreb","unit":"celsius"}""");

    private sealed record ErrorSetup(ToolEnvelopeTurn Turn, string Output);
}

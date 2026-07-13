using System.Text.Json;

namespace LlamaSharp.ToolCallEnvelopes.Tests;

[TestFixture]
public sealed class PublicExceptionGuardTests
{
    private static readonly string[] CaseNames =
    [
        "definition-null-name",
        "definition-invalid-name",
        "definition-null-description",
        "definition-blank-description",
        "definition-control-description",
        "definition-non-object-schema",
        "definition-disposed-schema",
        "definition-null-json",
        "definition-malformed-json",
        "choice-null-name",
        "choice-invalid-name",
        "message-null-text",
        "message-blank-text",
        "calls-null",
        "calls-empty",
        "calls-null-entry",
        "calls-noncontiguous",
        "result-null-call",
        "result-null-content",
        "plan-null-catalog",
        "plan-null-entry",
        "plan-null-limits",
        "turn-null-system",
        "turn-null-history",
        "turn-null-history-entry",
        "turn-system-history",
        "turn-required-without-tools",
        "turn-unknown-named-tool",
        "call-negative-index",
        "call-index-over-limit",
        "call-null-name",
        "call-undefined-arguments",
        "call-disposed-arguments",
        "parse-null-output",
        "stream-null-fragment",
        "stream-complete-invalid-output",
        "stream-complete-twice",
        "stream-feed-after-completion",
        "stream-feed-after-rejection",
        "runner-null-executor",
        "runner-null-plan",
        "runner-null-system",
        "runner-null-history",
        "runner-null-dispatch",
        "runner-null-initial-choice",
        "runner-null-follow-up-choice",
        "runner-invalid-follow-up-choice",
        "runner-zero-turns",
        "runner-zero-attempts",
    ];

    public static IEnumerable<TestCaseData> EveryPublicGuard =>
        CaseNames.Select(name => new TestCaseData(name).SetName($"Guard_{name}"));

    [TestCaseSource(nameof(EveryPublicGuard))]
    public void EveryPublicGuard_ExplainsTheInvalidValueAndNextAction(string caseName)
    {
        var guard = CreateCase(caseName);

        Exception? exception = null;
        try
        {
            guard.Action();
        }
        catch (Exception caught)
        {
            exception = caught;
        }

        exception.Should().NotBeNull($"guard case '{caseName}' must throw");
        guard.ExceptionType.IsInstanceOfType(exception).Should().BeTrue(
            $"guard case '{caseName}' should throw {guard.ExceptionType.Name}, but threw "
            + exception!.GetType().Name);
        exception!.Message.Should().Contain(guard.RequiredText)
            .And.MatchRegex("(?i)(supply|use|pass|replace|remove|create|compile|return|retry|fix|"
                + "correct|rehydrate|report|set|raise|reduce|inspect|yield|apply|provide)");
        exception.Message.Length.Should().BeGreaterThan(60);
        if (exception is ArgumentException argument)
            argument.ParamName.Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public void PublicGuardMatrix_HasUniqueCases()
    {
        CaseNames.Should().OnlyHaveUniqueItems().And.HaveCount(49);
    }

    private static GuardCase CreateCase(string name)
    {
        var plan = TestCatalog.Plan(maxCalls: 2);
        var call0 = plan.CreateCall(
            0,
            "get_weather",
            TestCatalog.Json("""{"city":"Zagreb","unit":"celsius"}"""));
        var call1 = plan.CreateCall(
            1,
            "get_weather",
            TestCatalog.Json("""{"city":"Split","unit":"celsius"}"""));

        return name switch
        {
            "definition-null-name" => Case(
                () => ToolDefinition.Create(null!, "Description.", ObjectSchema),
                typeof(ArgumentNullException),
                "tool name is required"),
            "definition-invalid-name" => Case(
                () => ToolDefinition.Create("bad name", "Description.", ObjectSchema),
                typeof(ArgumentException),
                "tool name is invalid"),
            "definition-null-description" => Case(
                () => ToolDefinition.Create("tool", null!, ObjectSchema),
                typeof(ArgumentNullException),
                "description is required"),
            "definition-blank-description" => Case(
                () => ToolDefinition.Create("tool", " ", ObjectSchema),
                typeof(ArgumentException),
                "no visible text"),
            "definition-control-description" => Case(
                () => ToolDefinition.Create("tool", "bad\nline", ObjectSchema),
                typeof(ArgumentException),
                "control character"),
            "definition-non-object-schema" => Case(
                () => ToolDefinition.Create("tool", "Description.", TestCatalog.Json("[]")),
                typeof(ArgumentException),
                "must be a JSON object"),
            "definition-disposed-schema" => DisposedSchemaCase(),
            "definition-null-json" => Case(
                () => ToolDefinition.Parse("tool", "Description.", null!),
                typeof(ArgumentNullException),
                "schema text cannot be null"),
            "definition-malformed-json" => Case(
                () => ToolDefinition.Parse("tool", "Description.", "{"),
                typeof(ArgumentException),
                "not one complete strict JSON object"),
            "choice-null-name" => Case(
                () => ToolChoice.Named(null!),
                typeof(ArgumentNullException),
                "tool name is required"),
            "choice-invalid-name" => Case(
                () => ToolChoice.Named("bad name"),
                typeof(ArgumentException),
                "tool name is invalid"),
            "message-null-text" => Case(
                () => ToolMessage.User(null!),
                typeof(ArgumentNullException),
                "Message text cannot be null"),
            "message-blank-text" => Case(
                () => ToolMessage.Assistant(" "),
                typeof(ArgumentException),
                "no visible content"),
            "calls-null" => Case(
                () => ToolMessage.AssistantCalls(null!),
                typeof(ArgumentNullException),
                "call collection cannot be null"),
            "calls-empty" => Case(
                () => ToolMessage.AssistantCalls([]),
                typeof(ArgumentException),
                "at least one validated call"),
            "calls-null-entry" => Case(
                () => ToolMessage.AssistantCalls([null!]),
                typeof(ArgumentException),
                "contains null at index 0"),
            "calls-noncontiguous" => Case(
                () => ToolMessage.AssistantCalls([call1]),
                typeof(ArgumentException),
                "reports call index 1"),
            "result-null-call" => Case(
                () => ToolMessage.ToolResult(null!, "{}"),
                typeof(ArgumentNullException),
                "validated ToolCall is required"),
            "result-null-content" => Case(
                () => ToolMessage.ToolResult(call0, null!),
                typeof(ArgumentNullException),
                "content cannot be null"),
            "plan-null-catalog" => Case(
                () => ToolEnvelopePlan.Compile(null!),
                typeof(ArgumentNullException),
                "tool catalog is required"),
            "plan-null-entry" => Case(
                () => ToolEnvelopePlan.Compile([null!]),
                typeof(ArgumentException),
                "contains null at index 0"),
            "plan-null-limits" => Case(
                () => ToolEnvelopePlan.Compile([], new ToolEnvelopePlanOptions { Limits = null! }),
                typeof(ToolEnvelopePlanException),
                "Limits cannot be null"),
            "turn-null-system" => Case(
                () => plan.CreateTurn(null!, []),
                typeof(ArgumentNullException),
                "system prompt cannot be null"),
            "turn-null-history" => Case(
                () => plan.CreateTurn("Policy.", null!),
                typeof(ArgumentNullException),
                "history cannot be null"),
            "turn-null-history-entry" => Case(
                () => plan.CreateTurn("Policy.", [null!]),
                typeof(ArgumentException),
                "contains null at index 0"),
            "turn-system-history" => Case(
                () => plan.CreateTurn("Policy.", [plan.CreateTurn("Old.", []).Prompt[0]]),
                typeof(ArgumentException),
                "has the System role"),
            "turn-required-without-tools" => Case(
                () => TestCatalog.Plan(tools: []).CreateTurn("Policy.", [], ToolChoice.Required),
                typeof(ArgumentException),
                "contains no tools"),
            "turn-unknown-named-tool" => Case(
                () => plan.CreateTurn("Policy.", [], ToolChoice.Named("missing")),
                typeof(ArgumentException),
                "not in this plan"),
            "call-negative-index" => Case(
                () => plan.CreateCall(-1, "get_weather", call0.Arguments),
                typeof(ArgumentOutOfRangeException),
                "cannot be negative"),
            "call-index-over-limit" => Case(
                () => plan.CreateCall(2, "get_weather", call0.Arguments),
                typeof(ArgumentOutOfRangeException),
                "between zero and 1"),
            "call-null-name" => Case(
                () => plan.CreateCall(0, null!, call0.Arguments),
                typeof(ArgumentNullException),
                "exact catalog tool name"),
            "call-undefined-arguments" => Case(
                () => plan.CreateCall(0, "get_weather", default),
                typeof(ToolEnvelopeException),
                "supplied value was Undefined"),
            "call-disposed-arguments" => DisposedArgumentsCase(plan),
            "parse-null-output" => Case(
                () => plan.CreateTurn("Policy.", []).Parse(null!),
                typeof(ArgumentNullException),
                "Model output cannot be null"),
            "stream-null-fragment" => Case(
                () => plan.CreateTurn("Policy.", []).CreateStreamReader().Feed(null!),
                typeof(ArgumentNullException),
                "fragment cannot be null"),
            "stream-complete-invalid-output" => StreamCompleteInvalidOutputCase(plan),
            "stream-complete-twice" => StreamCompleteTwiceCase(plan),
            "stream-feed-after-completion" => StreamFeedAfterCompletionCase(plan),
            "stream-feed-after-rejection" => StreamFeedAfterRejectionCase(plan),
            "runner-null-executor" => Case(
                () => ToolEnvelopeRunner.RunAsync(
                    null!, plan, "Policy.", [], (_, _) => ValueTask.FromResult("{}")),
                typeof(ArgumentNullException),
                "model executor is required"),
            "runner-null-plan" => Case(
                () => ToolEnvelopeRunner.RunAsync(
                    new QueueExecutor("""{"text":"done"}"""),
                    null!,
                    "Policy.",
                    [],
                    (_, _) => ValueTask.FromResult("{}")),
                typeof(ArgumentNullException),
                "compiled ToolEnvelopePlan is required"),
            "runner-null-system" => Case(
                () => ToolEnvelopeRunner.RunAsync(
                    new QueueExecutor("""{"text":"done"}"""),
                    plan,
                    null!,
                    [],
                    (_, _) => ValueTask.FromResult("{}")),
                typeof(ArgumentNullException),
                "system prompt cannot be null"),
            "runner-null-history" => Case(
                () => ToolEnvelopeRunner.RunAsync(
                    new QueueExecutor("""{"text":"done"}"""),
                    plan,
                    "Policy.",
                    null!,
                    (_, _) => ValueTask.FromResult("{}")),
                typeof(ArgumentNullException),
                "messages are required"),
            "runner-null-dispatch" => Case(
                () => ToolEnvelopeRunner.RunAsync(
                    new QueueExecutor("""{"text":"done"}"""),
                    plan,
                    "Policy.",
                    [],
                    null!),
                typeof(ArgumentNullException),
                "tool dispatcher is required"),
            "runner-null-initial-choice" => RunnerOptionsCase(
                plan,
                options: new ToolRunOptions { InitialChoice = null! }),
            "runner-null-follow-up-choice" => RunnerOptionsCase(
                plan,
                options: new ToolRunOptions { FollowUpChoice = null! }),
            "runner-invalid-follow-up-choice" => RunnerOptionsCase(
                plan,
                options: new ToolRunOptions { FollowUpChoice = ToolChoice.Required }),
            "runner-zero-turns" => RunnerOptionsCase(
                plan,
                options: new ToolRunOptions { MaxModelTurns = 0 }),
            "runner-zero-attempts" => RunnerOptionsCase(
                plan,
                options: new ToolRunOptions { MaxAttemptsPerTurn = 0 }),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown guard case."),
        };
    }

    private static GuardCase DisposedSchemaCase()
    {
        JsonElement schema;
        using (var document = JsonDocument.Parse("""{"type":"object"}"""))
            schema = document.RootElement;
        return Case(
            () => ToolDefinition.Create("tool", "Description.", schema),
            typeof(ArgumentException),
            "owning JsonDocument has already been disposed");
    }

    private static GuardCase DisposedArgumentsCase(ToolEnvelopePlan plan)
    {
        JsonElement arguments;
        using (var document = JsonDocument.Parse(
                   """{"city":"Zagreb","unit":"celsius"}"""))
        {
            arguments = document.RootElement;
        }

        return Case(
            () => plan.CreateCall(0, "get_weather", arguments),
            typeof(ArgumentException),
            "persisted call arguments cannot be read");
    }

    private static GuardCase StreamCompleteTwiceCase(ToolEnvelopePlan plan)
    {
        var reader = plan.CreateTurn("Policy.", []).CreateStreamReader();
        reader.Feed("""{"text":"done"}""");
        reader.Complete();
        return Case(
            () => reader.Complete(),
            typeof(InvalidOperationException),
            "already been called");
    }

    private static GuardCase StreamCompleteInvalidOutputCase(ToolEnvelopePlan plan)
    {
        var reader = plan.CreateTurn("Policy.", []).CreateStreamReader();
        reader.Feed("not json");
        return Case(
            () => reader.Complete(),
            typeof(ToolEnvelopeException),
            "model response is invalid");
    }

    private static GuardCase StreamFeedAfterCompletionCase(ToolEnvelopePlan plan)
    {
        var reader = plan.CreateTurn("Policy.", []).CreateStreamReader();
        reader.Feed("""{"text":"done"}""");
        reader.Complete();
        return Case(
            () => reader.Feed("more"),
            typeof(InvalidOperationException),
            "already completed successfully");
    }

    private static GuardCase StreamFeedAfterRejectionCase(ToolEnvelopePlan plan)
    {
        var reader = plan.CreateTurn("Policy.", []).CreateStreamReader();
        reader.Feed("not json");
        reader.TryComplete(out _, out _);
        return Case(
            () => reader.Feed("more"),
            typeof(InvalidOperationException),
            "already rejected the response");
    }

    private static GuardCase RunnerOptionsCase(
        ToolEnvelopePlan plan,
        ToolRunOptions options)
    {
        Action action = () => ToolEnvelopeRunner.RunAsync(
            new QueueExecutor("""{"text":"done"}"""),
            plan,
            "Policy.",
            [],
            (_, _) => ValueTask.FromResult("{}"),
            options);
        var requiredText = options.InitialChoice is null
            ? "InitialChoice is required"
            : options.FollowUpChoice is null
                ? "FollowUpChoice is required"
                : options.FollowUpChoice.Kind == ToolChoiceKind.Required
                    ? "accept only ToolChoice.Auto or ToolChoice.None"
                    : options.MaxModelTurns == 0
                        ? "MaxModelTurns must be greater than zero"
                        : "MaxAttemptsPerTurn must be greater than zero";
        return Case(action, typeof(ArgumentException), requiredText);
    }

    private static GuardCase Case(Action action, Type exceptionType, string requiredText) =>
        new(action, exceptionType, requiredText);

    private static JsonElement ObjectSchema => TestCatalog.Json("""
        {
          "type": "object",
          "properties": {},
          "additionalProperties": false
        }
        """);

    private sealed record GuardCase(Action Action, Type ExceptionType, string RequiredText);
}

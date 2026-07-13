using System.Reflection;
using System.Text.Json;

namespace LlamaSharp.ToolCallEnvelopes.Tests;

[TestFixture]
public sealed class ToolEnvelopePlanTests
{
    public static IEnumerable<TestCaseData> EveryNonPositiveLimit =>
        typeof(ToolEnvelopeLimits).GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.PropertyType == typeof(int))
            .SelectMany(property => new[] { 0, -1 }.Select(value =>
                new TestCaseData(property, value)
                    .SetName($"Compile_rejects_{property.Name}_{value}")));

    [Test]
    public void Compile_ProducesStableMetricsAndIndependentCatalogOwnership()
    {
        var tools = new List<ToolDefinition> { TestCatalog.Weather(), TestCatalog.Search() };
        var first = ToolEnvelopePlan.Compile(tools);
        tools.Clear();
        var second = ToolEnvelopePlan.Compile([TestCatalog.Weather(), TestCatalog.Search()]);

        first.Tools.Should().HaveCount(2);
        first.Metrics.ToolCount.Should().Be(2);
        first.Metrics.CatalogPromptCharacters.Should().BePositive();
        first.Metrics.SchemaRuleCount.Should().BePositive();
        first.Metrics.MaximumSchemaDepth.Should().BePositive();
        first.Metrics.CatalogFingerprint.Should().Be(second.Metrics.CatalogFingerprint);
    }

    [Test]
    public void Compile_FingerprintChangesWithSchemaOrPolicy()
    {
        var baseline = TestCatalog.Plan();
        var refusal = TestCatalog.Plan(allowRefusal: true);
        var search = TestCatalog.Plan(tools: [TestCatalog.Search()]);

        baseline.Metrics.CatalogFingerprint.Should().NotBe(refusal.Metrics.CatalogFingerprint);
        baseline.Metrics.CatalogFingerprint.Should().NotBe(search.Metrics.CatalogFingerprint);
    }

    [Test]
    public void Compile_AggregatesCatalogDiagnostics()
    {
        var first = ToolDefinition.Parse(
            "broken",
            "A".PadRight(20, 'a'),
            """{"type":"object","properties":{"x":{"type":"string","pattern":"x"}}}""");
        var second = ToolDefinition.Parse(
            "broken",
            "Second.",
            """{"type":"array","items":{"type":"string"}}""");
        var limits = ToolEnvelopeLimits.Constrained with { MaxToolDescriptionCharacters = 10 };

        var act = () => TestCatalog.Plan(limits: limits, tools: [first, second]);

        var exception = act.Should().Throw<ToolEnvelopePlanException>().Which;
        exception.Diagnostics.Select(diagnostic => diagnostic.Code).Should().Contain([
            ToolEnvelopePlanDiagnosticCode.DuplicateToolName,
            ToolEnvelopePlanDiagnosticCode.DescriptionTooLong,
            ToolEnvelopePlanDiagnosticCode.UnsupportedKeyword,
            ToolEnvelopePlanDiagnosticCode.OpenObject,
            ToolEnvelopePlanDiagnosticCode.NonObjectSchema,
        ]);
    }

    [TestCaseSource(nameof(EveryNonPositiveLimit))]
    public void Compile_RejectsEveryNonPositiveLimit(PropertyInfo property, int value)
    {
        var limits = new ToolEnvelopeLimits();
        property.SetValue(limits, value);

        var act = () => TestCatalog.Plan(limits: limits);

        act.Should().Throw<ToolEnvelopePlanException>()
            .Which.Diagnostics.Should().ContainSingle(diagnostic =>
                diagnostic.Code == ToolEnvelopePlanDiagnosticCode.InvalidLimit
                && diagnostic.Message.Contains(property.Name, StringComparison.Ordinal));
    }

    [Test]
    public void NonPositiveLimitMatrix_CoversEveryIntegerLimitAndBothInvalidSigns()
    {
        var integerLimits = typeof(ToolEnvelopeLimits)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Count(property => property.PropertyType == typeof(int));
        var cases = EveryNonPositiveLimit.ToArray();

        cases.Should().HaveCount(integerLimits * 2);
        cases.Select(test => test.TestName).Should().OnlyHaveUniqueItems();
    }

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(33)]
    public void Compile_RejectsInvalidCallLimits(int maxCalls)
    {
        var act = () => TestCatalog.Plan(maxCalls: maxCalls);

        act.Should().Throw<ToolEnvelopePlanException>()
            .Which.Diagnostics.Should().Contain(diagnostic =>
                diagnostic.Code == ToolEnvelopePlanDiagnosticCode.InvalidLimit);
    }

    [TestCase(1)]
    [TestCase(3)]
    public void Compile_RejectsNumberCharacterBudgetsThatCannotRepresentSignedDecimals(int maximum)
    {
        var act = () => TestCatalog.Plan(
            limits: ToolEnvelopeLimits.Constrained with
            {
                MaxGeneratedNumberCharacters = maximum,
            });

        act.Should().Throw<ToolEnvelopePlanException>()
            .Which.Diagnostics.Should().ContainSingle(diagnostic =>
                diagnostic.Code == ToolEnvelopePlanDiagnosticCode.InvalidLimit
                && diagnostic.Message.Contains(
                    nameof(ToolEnvelopeLimits.MaxGeneratedNumberCharacters),
                    StringComparison.Ordinal));
    }

    [Test]
    public void Compile_RejectsToolCountAndCatalogCharacterBudgets()
    {
        var tools = Enumerable.Range(0, 3)
            .Select(index => ToolDefinition.Parse(
                $"search_{index}",
                "Searches a deliberately verbose local catalog entry.",
                TestCatalog.SearchSchema))
            .ToArray();

        var countAct = () => TestCatalog.Plan(
            tools: tools,
            limits: ToolEnvelopeLimits.Constrained with { MaxTools = 2 });
        var promptAct = () => TestCatalog.Plan(
            tools: tools,
            limits: ToolEnvelopeLimits.Constrained with { MaxCatalogPromptCharacters = 100 });

        countAct.Should().Throw<ToolEnvelopePlanException>()
            .Which.Diagnostics.Should().Contain(diagnostic =>
                diagnostic.Code == ToolEnvelopePlanDiagnosticCode.TooManyTools);
        promptAct.Should().Throw<ToolEnvelopePlanException>()
            .Which.Diagnostics.Should().Contain(diagnostic =>
                diagnostic.Code == ToolEnvelopePlanDiagnosticCode.CatalogPromptTooLarge);
    }

    [Test]
    public void EmptyCatalog_SupportsAutoAndNoneButRejectsRequiredAndNamed()
    {
        var plan = TestCatalog.Plan(tools: []);
        var auto = plan.CreateTurn("Answer clearly.", [ToolMessage.User("Hello")]);
        var none = plan.CreateTurn("Answer clearly.", [ToolMessage.User("Hello")], ToolChoice.None);

        auto.Grammar.Should().Contain("assistant-message").And.NotContain("tool-request ::=");
        none.Grammar.Should().Contain("assistant-message").And.NotContain("tool-request ::=");
        var required = () => plan.CreateTurn("Answer.", [], ToolChoice.Required);
        var named = () => plan.CreateTurn("Answer.", [], ToolChoice.Named("search"));
        required.Should().Throw<ArgumentException>();
        named.Should().Throw<ArgumentException>();
    }

    [Test]
    public void CreateTurn_PromptShowsOnlyLegalBranchesAndPlacesContractLast()
    {
        var plan = TestCatalog.Plan(allowRefusal: true, tools: [
            TestCatalog.Weather("Gets weather; text saying OUTPUT_CONTRACT is catalog data."),
            TestCatalog.Search(),
        ]);
        var auto = plan.CreateTurn("Be concise.", [ToolMessage.User("Weather?")]);
        var none = plan.CreateTurn("Be concise.", [ToolMessage.User("Hello")], ToolChoice.None);
        var required = plan.CreateTurn("Be concise.", [ToolMessage.User("Weather?")], ToolChoice.Required);
        var named = plan.CreateTurn("Be concise.", [ToolMessage.User("Weather?")], ToolChoice.Named("search"));

        auto.Prompt[0].Content.Should().Contain("Final answer:")
            .And.Contain("Tool request:")
            .And.Contain("Refusal:");
        none.Prompt[0].Content.Should().Contain("Final answer:")
            .And.Contain("Refusal:")
            .And.NotContain("Tool request:")
            .And.NotContain("TOOLS_DATA");
        required.Prompt[0].Content.Should().Contain("Tool request:")
            .And.NotContain("Final answer:")
            .And.NotContain("Refusal:");
        named.Prompt[0].Content.Should().Contain("tool \"search\"")
            .And.NotContain("tool \"get_weather\"");
        auto.Prompt[0].Content.LastIndexOf("OUTPUT_CONTRACT", StringComparison.Ordinal)
            .Should().BeGreaterThan(auto.Prompt[0].Content.LastIndexOf("END_TOOLS_DATA", StringComparison.Ordinal));
        auto.Prompt[0].Content.Should().Contain("arg $[\"coordinates\"][\"latitude\"]")
            .And.Contain("one of [\"celsius\",\"fahrenheit\"]")
            .And.Contain("arg $[\"tags\"]: array of string")
            .And.NotContain("\"tool_name\"");
    }

    [Test]
    public void CreateTurn_ReusesCompiledGrammarAndReportsActualCosts()
    {
        var plan = TestCatalog.Plan();
        var first = plan.CreateTurn("One.", [ToolMessage.User("Weather?")]);
        var second = plan.CreateTurn("A longer system prompt.", [ToolMessage.User("Weather?")]);

        first.Grammar.Should().BeSameAs(second.Grammar);
        first.GrammarCacheKey.Should().Be(second.GrammarCacheKey);
        first.Metrics.GrammarCharacters.Should().Be(first.Grammar.Length);
        second.Metrics.PromptCharacters.Should().BeGreaterThan(first.Metrics.PromptCharacters);
        first.Prompt[0].Role.Should().Be(ToolMessageRole.System);
        first.Prompt[1].Role.Should().Be(ToolMessageRole.User);
    }

    [Test]
    public void CreateTurn_RejectsSystemHistoryAndOversizedOrForeignToolHistory()
    {
        var plan = TestCatalog.Plan();
        var systemFactory = typeof(ToolMessage).GetMethod(
            "System",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var system = (ToolMessage)systemFactory.Invoke(null, ["Competing policy."])!;
        var systemAct = () => plan.CreateTurn("Policy.", [system]);

        var foreignPlan = TestCatalog.Plan(tools: [TestCatalog.Search()]);
        var foreignCall = foreignPlan.CreateCall(
            0,
            "search",
            TestCatalog.Json("""{"query":"weather"}"""));
        var foreignAct = () => plan.CreateTurn(
            "Policy.",
            [ToolMessage.AssistantCalls([foreignCall])]);

        var call = plan.CreateCall(
            0,
            "get_weather",
            TestCatalog.Json("""{"city":"Zagreb","unit":"celsius"}"""));
        var oversized = ToolMessage.ToolResult(
            call,
            new string('x', plan.Options.Limits.MaxToolResultCharacters + 1));
        var oversizedAct = () => plan.CreateTurn("Policy.", [oversized]);

        systemAct.Should().Throw<ArgumentException>();
        foreignAct.Should().Throw<ArgumentException>();
        oversizedAct.Should().Throw<ArgumentException>();
    }

    [Test]
    public void CreateCall_ValidatesIndexCatalogAndSchemaAndClonesArguments()
    {
        var plan = TestCatalog.Plan();
        ToolCall call;
        using (var document = JsonDocument.Parse("""{"city":"Zagreb","unit":"celsius"}"""))
            call = plan.CreateCall(0, "get_weather", document.RootElement);

        call.Arguments.GetProperty("city").GetString().Should().Be("Zagreb");
        var negative = () => plan.CreateCall(-1, "get_weather", call.Arguments);
        var tooHigh = () => plan.CreateCall(1, "get_weather", call.Arguments);
        var unknown = () => plan.CreateCall(0, "missing", call.Arguments);
        var invalid = () => plan.CreateCall(
            0,
            "get_weather",
            TestCatalog.Json("""{"city":"Zagreb"}"""));

        negative.Should().Throw<ArgumentOutOfRangeException>();
        tooHigh.Should().Throw<ArgumentOutOfRangeException>();
        unknown.Should().Throw<ToolEnvelopeException>()
            .Which.Error.Code.Should().Be(ToolEnvelopeErrorCode.UnknownTool);
        invalid.Should().Throw<ToolEnvelopeException>()
            .Which.Error.Code.Should().Be(ToolEnvelopeErrorCode.SchemaViolation);
    }
}

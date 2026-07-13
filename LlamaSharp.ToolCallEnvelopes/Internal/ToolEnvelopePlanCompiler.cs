using System.Collections.ObjectModel;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace LlamaSharp.ToolCallEnvelopes.Internal;

internal static class ToolEnvelopePlanCompiler
{
    internal static ToolEnvelopePlan Compile(
        IEnumerable<ToolDefinition> tools,
        ToolEnvelopePlanOptions? options)
    {
        Guard.NotNull(
            tools,
            nameof(tools),
            "A tool catalog is required. Pass an empty collection when the plan should support "
            + "only final text and optional refusals.");
        options ??= new ToolEnvelopePlanOptions();

        var diagnostics = new List<ToolEnvelopePlanDiagnostic>();
        ValidateOptions(options, diagnostics);
        ThrowIfInvalid(diagnostics);

        var toolArray = tools.ToArray();
        var nullIndex = Array.FindIndex(toolArray, tool => tool is null);
        if (nullIndex >= 0)
        {
            throw new ArgumentException(
                $"The tool catalog contains null at index {nullIndex}. Replace it with a "
                + "ToolDefinition or remove that entry before compiling the plan.",
                nameof(tools));
        }

        ValidateCatalog(toolArray, options, diagnostics);
        var compiledTools = CompileSchemas(toolArray, options, diagnostics);
        ThrowIfInvalid(diagnostics);

        var readOnlyTools = Array.AsReadOnly(toolArray);
        var readOnlyCompiled = Array.AsReadOnly(compiledTools.ToArray());
        var catalog = SemanticPromptBuilder.BuildCatalog(readOnlyCompiled);
        if (catalog.Length > options.Limits.MaxCatalogPromptCharacters)
        {
            diagnostics.Add(new ToolEnvelopePlanDiagnostic(
                ToolEnvelopePlanDiagnosticCode.CatalogPromptTooLarge,
                $"The semantic catalog uses {catalog.Length} characters; the plan limit is "
                + $"{options.Limits.MaxCatalogPromptCharacters}.",
                string.Empty));
            ThrowIfInvalid(diagnostics);
        }

        var fingerprint = CanonicalJson.ComputeCatalogFingerprint(readOnlyTools, options);
        var variants = CompileVariants(readOnlyCompiled, options, fingerprint);
        var metrics = new ToolEnvelopePlanMetrics(
            fingerprint,
            readOnlyTools.Count,
            catalog.Length,
            readOnlyCompiled.Sum(tool => tool.SchemaRuleCount),
            readOnlyCompiled.Count == 0
                ? 0
                : readOnlyCompiled.Max(tool => tool.MaximumSchemaDepth));

        return new ToolEnvelopePlan(
            readOnlyTools,
            readOnlyCompiled,
            options,
            metrics,
            variants);
    }

    private static void ValidateCatalog(
        IReadOnlyList<ToolDefinition> tools,
        ToolEnvelopePlanOptions options,
        List<ToolEnvelopePlanDiagnostic> diagnostics)
    {
        if (tools.Count > options.Limits.MaxTools)
        {
            diagnostics.Add(new ToolEnvelopePlanDiagnostic(
                ToolEnvelopePlanDiagnosticCode.TooManyTools,
                $"The catalog has {tools.Count} tools; the plan limit is "
                + $"{options.Limits.MaxTools}.",
                string.Empty));
        }

        foreach (var duplicate in tools
                     .GroupBy(tool => tool.Name, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            diagnostics.Add(new ToolEnvelopePlanDiagnostic(
                ToolEnvelopePlanDiagnosticCode.DuplicateToolName,
                $"Tool name '{duplicate.Key}' is repeated.",
                string.Empty,
                duplicate.Key));
        }

        foreach (var tool in tools)
        {
            if (tool.Description.Length > options.Limits.MaxToolDescriptionCharacters)
            {
                diagnostics.Add(new ToolEnvelopePlanDiagnostic(
                    ToolEnvelopePlanDiagnosticCode.DescriptionTooLong,
                    $"The tool description has {tool.Description.Length} characters; the plan "
                    + $"limit is {options.Limits.MaxToolDescriptionCharacters}.",
                    string.Empty,
                    tool.Name));
            }
        }
    }

    private static List<CompiledTool> CompileSchemas(
        IReadOnlyList<ToolDefinition> tools,
        ToolEnvelopePlanOptions options,
        List<ToolEnvelopePlanDiagnostic> diagnostics)
    {
        var compiledTools = new List<CompiledTool>(tools.Count);
        foreach (var tool in tools)
        {
            var schemaCharacters = tool.Parameters.GetRawText().Length;
            if (schemaCharacters > options.Limits.MaxToolSchemaCharacters)
            {
                diagnostics.Add(new ToolEnvelopePlanDiagnostic(
                    ToolEnvelopePlanDiagnosticCode.SchemaTooLarge,
                    $"The tool schema has {schemaCharacters} characters; the plan limit is "
                    + $"{options.Limits.MaxToolSchemaCharacters}.",
                    string.Empty,
                    tool.Name));
                continue;
            }

            var compiled = new SchemaCompiler(tool, options.Limits, diagnostics).Compile();
            if (compiled is not null)
                compiledTools.Add(compiled);
        }

        return compiledTools;
    }

    private static IReadOnlyDictionary<ToolChoice, CompiledTurnVariant> CompileVariants(
        IReadOnlyList<CompiledTool> tools,
        ToolEnvelopePlanOptions options,
        string fingerprint)
    {
        var choices = new List<ToolChoice> { ToolChoice.Auto, ToolChoice.None };
        if (tools.Count > 0)
        {
            choices.Add(ToolChoice.Required);
            choices.AddRange(tools.Select(tool => ToolChoice.Named(tool.Definition.Name)));
        }

        var variants = new Dictionary<ToolChoice, CompiledTurnVariant>();
        foreach (var choice in choices)
        {
            var selected = choice.Kind switch
            {
                ToolChoiceKind.None => Array.Empty<CompiledTool>(),
                ToolChoiceKind.Named => tools.Where(tool => string.Equals(
                        tool.Definition.Name,
                        choice.ToolName,
                        StringComparison.Ordinal))
                    .ToArray(),
                _ => tools.ToArray(),
            };
            var readOnlySelected = Array.AsReadOnly(selected);
            var grammar = GrammarBuilder.Build(choice, tools, options);
            var cacheKey = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes($"{fingerprint}|{choice}")));
            variants.Add(choice, new CompiledTurnVariant(
                choice,
                readOnlySelected,
                SemanticPromptBuilder.BuildCatalog(readOnlySelected),
                SemanticPromptBuilder.BuildOutputContract(choice, readOnlySelected, options),
                grammar,
                cacheKey));
        }

        return new ReadOnlyDictionary<ToolChoice, CompiledTurnVariant>(variants);
    }

    private static void ValidateOptions(
        ToolEnvelopePlanOptions options,
        List<ToolEnvelopePlanDiagnostic> diagnostics)
    {
        if (options.Limits is null)
        {
            diagnostics.Add(new ToolEnvelopePlanDiagnostic(
                ToolEnvelopePlanDiagnosticCode.InvalidLimit,
                "ToolEnvelopePlanOptions.Limits cannot be null. Use "
                + "ToolEnvelopeLimits.Constrained or provide a complete limits object.",
                string.Empty));
            return;
        }

        foreach (var property in typeof(ToolEnvelopeLimits).GetProperties(
                     BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.PropertyType == typeof(int)
                && (int)property.GetValue(options.Limits)! <= 0)
            {
                diagnostics.Add(new ToolEnvelopePlanDiagnostic(
                    ToolEnvelopePlanDiagnosticCode.InvalidLimit,
                    $"ToolEnvelopeLimits.{property.Name} must be greater than zero; the configured "
                    + $"value is {property.GetValue(options.Limits)}.",
                    string.Empty));
            }
        }

        if (options.Limits.MaxGeneratedNumberCharacters is > 0 and < 4)
        {
            diagnostics.Add(new ToolEnvelopePlanDiagnostic(
                ToolEnvelopePlanDiagnosticCode.InvalidLimit,
                "ToolEnvelopeLimits.MaxGeneratedNumberCharacters must be at least four so the "
                + "grammar can represent the smallest bounded signed decimal form.",
                string.Empty));
        }

        if (options.MaxCallsPerTurn <= 0)
        {
            diagnostics.Add(new ToolEnvelopePlanDiagnostic(
                ToolEnvelopePlanDiagnosticCode.InvalidLimit,
                $"ToolEnvelopePlanOptions.MaxCallsPerTurn must be greater than zero; the configured "
                + $"value is {options.MaxCallsPerTurn}.",
                string.Empty));
        }
        else if (options.Limits.MaxGeneratedArrayItems > 0
                 && options.MaxCallsPerTurn > options.Limits.MaxGeneratedArrayItems)
        {
            diagnostics.Add(new ToolEnvelopePlanDiagnostic(
                ToolEnvelopePlanDiagnosticCode.InvalidLimit,
                $"ToolEnvelopePlanOptions.MaxCallsPerTurn is {options.MaxCallsPerTurn}, but it "
                + "cannot exceed ToolEnvelopeLimits.MaxGeneratedArrayItems "
                + $"({options.Limits.MaxGeneratedArrayItems}).",
                string.Empty));
        }
    }

    private static void ThrowIfInvalid(List<ToolEnvelopePlanDiagnostic> diagnostics)
    {
        if (diagnostics.Count > 0)
            throw new ToolEnvelopePlanException(Array.AsReadOnly(diagnostics.ToArray()));
    }
}

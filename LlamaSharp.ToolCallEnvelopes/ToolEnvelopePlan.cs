using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using LlamaSharp.ToolCallEnvelopes.Internal;

namespace LlamaSharp.ToolCallEnvelopes;

/// <summary>
/// An immutable catalog, prompt, grammar, parser, and argument-validation contract.
/// </summary>
public sealed class ToolEnvelopePlan
{
    private readonly IReadOnlyList<CompiledTool> _compiledTools;
    private readonly IReadOnlyDictionary<string, CompiledTool> _toolsByName;
    private readonly IReadOnlyDictionary<ToolChoice, CompiledTurnVariant> _variants;

    internal ToolEnvelopePlan(
        IReadOnlyList<ToolDefinition> tools,
        IReadOnlyList<CompiledTool> compiledTools,
        ToolEnvelopePlanOptions options,
        ToolEnvelopePlanMetrics metrics,
        IReadOnlyDictionary<ToolChoice, CompiledTurnVariant> variants)
    {
        Tools = tools;
        _compiledTools = compiledTools;
        Options = options;
        Metrics = metrics;
        _variants = variants;
        _toolsByName = new ReadOnlyDictionary<string, CompiledTool>(
            compiledTools.ToDictionary(tool => tool.Definition.Name, StringComparer.Ordinal));
    }

    /// <summary>The independently owned catalog.</summary>
    public IReadOnlyList<ToolDefinition> Tools { get; }

    /// <summary>The immutable plan options.</summary>
    public ToolEnvelopePlanOptions Options { get; }

    /// <summary>Measured catalog and schema costs.</summary>
    public ToolEnvelopePlanMetrics Metrics { get; }

    /// <summary>Compiles a complete reusable plan or reports all discovered diagnostics.</summary>
    public static ToolEnvelopePlan Compile(
        IEnumerable<ToolDefinition> tools,
        ToolEnvelopePlanOptions? options = null) =>
        ToolEnvelopePlanCompiler.Compile(tools, options);

    /// <summary>
    /// Creates one manual turn whose prompt, grammar, and parser cannot drift.
    /// The host remains responsible for inference, retries, dispatch, and history.
    /// </summary>
    public ToolEnvelopeTurn CreateTurn(
        string systemPrompt,
        IEnumerable<ToolMessage> messages,
        ToolChoice? choice = null)
    {
        Guard.NotNull(
            systemPrompt,
            nameof(systemPrompt),
            "The system prompt cannot be null. Pass an empty string only when the package's "
            + "generated envelope policy is sufficient for the application.");
        Guard.NotNull(
            messages,
            nameof(messages),
            "Prompt history cannot be null. Pass an empty collection for a new conversation.");
        choice ??= ToolChoice.Auto;

        var messageArray = messages.ToArray();
        var nullIndex = Array.FindIndex(messageArray, message => message is null);
        if (nullIndex >= 0)
        {
            throw new ArgumentException(
                $"Prompt history contains null at index {nullIndex}. Replace it with a ToolMessage "
                + "created by User, Assistant, AssistantCalls, or ToolResult, or remove the entry.",
                nameof(messages));
        }

        ValidateHistory(messageArray);
        return CreateTurnCore(systemPrompt, messageArray, choice, repair: null);
    }

    /// <summary>
    /// Rehydrates a persisted call only after catalog and schema validation.
    /// </summary>
    public ToolCall CreateCall(int index, string name, JsonElement arguments)
    {
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index),
                index,
                "A persisted call index cannot be negative. Supply its zero-based position in the "
                + "original model tool_calls array.");
        }
        if (index >= Options.MaxCallsPerTurn)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index),
                index,
                $"The persisted call index must be between zero and "
                + $"{Options.MaxCallsPerTurn - 1} for this plan. Recompile with a larger "
                + "MaxCallsPerTurn only when the application intentionally supports larger batches.");
        }
        Guard.NotNull(
            name,
            nameof(name),
            "A persisted call needs the exact catalog tool name so this plan can validate its "
            + "arguments before the call enters history or dispatch. Supply the name stored with "
            + "the original call.");

        try
        {
            if (!TryCreateCall(index, name, arguments, "/arguments", out var call, out var error))
                throw new ToolEnvelopeException(error!);
            return call!;
        }
        catch (ObjectDisposedException exception)
        {
            throw new ArgumentException(
                "The persisted call arguments cannot be read because their owning JsonDocument "
                + "has already been disposed. Clone the JsonElement while its document is open, "
                + "or parse the persisted argument JSON immediately before calling CreateCall; "
                + "this plan will retain an independent validated copy.",
                nameof(arguments),
                exception);
        }
    }

    internal ToolEnvelopeTurn CreateRepairTurn(
        string systemPrompt,
        IReadOnlyList<ToolMessage> messages,
        ToolChoice choice,
        ToolEnvelopeError error,
        string invalidOutput) =>
        CreateTurnCore(
            systemPrompt,
            messages,
            choice,
            new RepairContext(error, CreateDiagnosticPreview(invalidOutput)));

    internal bool TryCreateCall(
        int index,
        string name,
        JsonElement arguments,
        string pointer,
        out ToolCall? call,
        out ToolEnvelopeError? error)
    {
        call = null;
        error = null;

        if (arguments.ValueKind != JsonValueKind.Object)
        {
            error = Error(
                ToolEnvelopeErrorCode.InvalidArguments,
                $"Expected tool arguments to be a JSON object, but the supplied value was "
                + $"{arguments.ValueKind}.",
                pointer,
                RawTextOrEmpty(arguments));
            return false;
        }

        if (!_toolsByName.TryGetValue(name, out var tool))
        {
            error = Error(
                ToolEnvelopeErrorCode.UnknownTool,
                $"Tool '{name}' is not in this plan.",
                pointer,
                arguments.GetRawText());
            return false;
        }

        if (!SchemaValidator.TryValidate(
                tool.Arguments,
                arguments,
                pointer,
                Options.Limits,
                out var violation))
        {
            error = Error(
                ToolEnvelopeErrorCode.SchemaViolation,
                violation!.Message,
                violation.JsonPointer,
                arguments.GetRawText());
            return false;
        }

        call = new ToolCall(index, name, arguments);
        return true;
    }

    internal bool ContainsTool(string name) => _toolsByName.ContainsKey(name);

    internal ToolEnvelopeError Error(
        ToolEnvelopeErrorCode code,
        string message,
        string pointer,
        string payload) =>
        new(
            code,
            ToolEnvelopeErrorMessages.Create(code, message, pointer),
            pointer,
            CreateDiagnosticPreview(payload));

    private ToolEnvelopeTurn CreateTurnCore(
        string systemPrompt,
        IReadOnlyList<ToolMessage> messages,
        ToolChoice choice,
        RepairContext? repair)
    {
        if (!_variants.TryGetValue(choice, out var variant))
        {
            var message = choice.Kind switch
            {
                ToolChoiceKind.Required when _compiledTools.Count == 0 =>
                    "ToolChoice.Required cannot be used because this plan contains no tools. Use "
                    + "ToolChoice.None or Auto for a final response, or compile the plan with at "
                    + "least one ToolDefinition.",
                ToolChoiceKind.Named =>
                    Tools.Count == 0
                        ? $"ToolChoice.Named refers to '{choice.ToolName}', but this plan contains "
                          + "no tools. Use ToolChoice.None or Auto for a final response, or compile "
                          + "the plan with the named ToolDefinition before creating the turn."
                        : $"ToolChoice.Named refers to '{choice.ToolName}', which is not in this "
                          + $"plan. Choose one of the compiled names: {string.Join(", ", Tools.Select(
                              tool => $"'{tool.Name}'"))}.",
                _ =>
                    $"Tool choice '{choice}' has no compiled prompt and grammar variant in this "
                    + "plan. Reuse ToolChoice.Auto, None, Required, or Named with a name from "
                    + "ToolEnvelopePlan.Tools.",
            };
            throw new ArgumentException(message, nameof(choice));
        }

        var system = SemanticPromptBuilder.BuildSystemMessage(
            systemPrompt,
            variant.Catalog,
            variant.OutputContract);
        var prompt = new List<ToolMessage>(messages.Count + (repair is null ? 1 : 3))
        {
            ToolMessage.System(system),
        };
        prompt.AddRange(messages);

        if (repair is not null)
        {
            prompt.Add(ToolMessage.RawAssistant(repair.InvalidOutputPreview));
            prompt.Add(ToolMessage.PackageUser(
                "RETRY_DATA\n"
                + $"error_code={repair.Error.Code}\n"
                + $"json_pointer={CreateDiagnosticPreview(repair.Error.JsonPointer)}\n"
                + $"message={CanonicalJson.SerializeString(
                    CreateDiagnosticPreview(repair.Error.Message))}\n"
                + "END_RETRY_DATA\n"
                + "Return one new object that follows OUTPUT_CONTRACT."));
        }

        var readOnlyPrompt = prompt.AsReadOnly();
        return new ToolEnvelopeTurn(
            this,
            variant,
            readOnlyPrompt,
            new ToolEnvelopeTurnMetrics(
                readOnlyPrompt.Sum(message => message.Content.Length),
                variant.Grammar.Length));
    }

    private void ValidateHistory(IReadOnlyList<ToolMessage> messages)
    {
        for (var messageIndex = 0; messageIndex < messages.Count; messageIndex++)
        {
            var message = messages[messageIndex];
            if (message.Role == ToolMessageRole.System)
            {
                throw new ArgumentException(
                    $"Prompt history message {messageIndex} has the System role. System policy must "
                    + "be supplied once through CreateTurn's systemPrompt argument so the package "
                    + "can append its matching output contract without competing system messages.",
                    nameof(messages));
            }

            if (message.Calls.Count > Options.MaxCallsPerTurn)
            {
                throw new ArgumentException(
                    $"Prompt history message {messageIndex} contains {message.Calls.Count} assistant "
                    + $"calls, but this plan permits at most {Options.MaxCallsPerTurn}. Split the "
                    + "batch or compile a deliberate larger limit before reusing this history.",
                    nameof(messages));
            }

            foreach (var call in message.Calls)
                ValidateHistoryCall(call, nameof(messages), $"message {messageIndex} call {call.Index}");

            if (message.AnsweredCall is { } answeredCall)
            {
                ValidateHistoryCall(
                    answeredCall,
                    nameof(messages),
                    $"tool-result message {messageIndex} call {answeredCall.Index}");
                if (message.ToolResultContent!.Length > Options.Limits.MaxToolResultCharacters)
                {
                    throw new ArgumentException(
                        $"Prompt history tool-result message {messageIndex} contains "
                        + $"{message.ToolResultContent.Length} result characters, but this plan "
                        + $"permits at most {Options.Limits.MaxToolResultCharacters}. Summarize or "
                        + "truncate the result before adding it to history, or deliberately raise "
                        + "MaxToolResultCharacters after checking context and memory budgets.",
                        nameof(messages));
                }
            }
        }
    }

    private void ValidateHistoryCall(
        ToolCall call,
        string parameterName,
        string historyLocation)
    {
        if (call.Index >= Options.MaxCallsPerTurn)
        {
            throw new ArgumentException(
                $"Prompt history {historyLocation} has index {call.Index}, but this plan permits "
                + $"indexes zero through {Options.MaxCallsPerTurn - 1}. Rehydrate persisted calls "
                + "with this plan's CreateCall method before adding them to history.",
                parameterName);
        }

        if (!TryCreateCall(
                call.Index,
                call.Name,
                call.Arguments,
                string.Empty,
                out _,
                out var error))
        {
            throw new ArgumentException(
                $"Prompt history {historyLocation} is incompatible with this plan. {error!.Message} "
                + "Rehydrate persisted calls with this plan's CreateCall method and reject history "
                + "that no longer matches the current catalog or schema.",
                parameterName);
        }
    }

    internal string CreateDiagnosticPreview(string payload)
    {
        var maximum = Options.Limits.MaxDiagnosticPreviewCharacters;
        var escaped = new StringBuilder(Math.Min(payload.Length, maximum));
        foreach (var character in payload)
        {
            var replacement = character switch
            {
                '\b' => "\\b",
                '\f' => "\\f",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when char.IsControl(character) => $"\\u{(int)character:X4}",
                _ => null,
            };
            var required = replacement?.Length ?? 1;
            if (escaped.Length + required > maximum)
                break;
            if (replacement is null)
                escaped.Append(character);
            else
                escaped.Append(replacement);
        }

        return escaped.ToString();
    }

    private static string RawTextOrEmpty(JsonElement value) =>
        value.ValueKind == JsonValueKind.Undefined ? string.Empty : value.GetRawText();

    private sealed record RepairContext(ToolEnvelopeError Error, string InvalidOutputPreview);
}

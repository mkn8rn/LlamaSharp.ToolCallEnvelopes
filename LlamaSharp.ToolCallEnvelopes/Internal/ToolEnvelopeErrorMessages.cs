namespace LlamaSharp.ToolCallEnvelopes.Internal;

internal static class ToolEnvelopeErrorMessages
{
    internal static string Create(
        ToolEnvelopeErrorCode code,
        string problem,
        string jsonPointer) =>
        $"The model response is invalid at {DisplayPointer(jsonPointer)} ({code}). {problem} "
        + $"Recovery: {RecoveryFor(code)}";

    private static string RecoveryFor(ToolEnvelopeErrorCode code) => code switch
    {
        ToolEnvelopeErrorCode.OutputTooLarge =>
            "reduce the generation budget, or deliberately raise MaxEnvelopeCharacters only after "
            + "checking model context and host memory. Retry with bounded backpressure or change the "
            + "model if it ignores the stop condition.",
        ToolEnvelopeErrorCode.TextTooLarge =>
            "ask for a shorter final answer, or deliberately raise MaxFinalTextCharacters after "
            + "checking model context and host memory. Retry with bounded backpressure or change the "
            + "model if it repeatedly exceeds the contract.",
        ToolEnvelopeErrorCode.RefusalTooLarge =>
            "ask for a shorter refusal, or deliberately raise MaxRefusalCharacters after checking "
            + "model context and host memory. Retry with bounded backpressure or change the model if "
            + "it repeatedly exceeds the contract.",
        ToolEnvelopeErrorCode.TooManyToolCalls =>
            "return no more than the turn's MaxCallsPerTurn value, split the work across managed "
            + "turns, or deliberately raise the limit. Retry with bounded backpressure before "
            + "changing models.",
        ToolEnvelopeErrorCode.UnknownTool =>
            "return an exact name from ToolEnvelopePlan.Tools. Repair or retry the response with "
            + "bounded backpressure, and change the model or prompt if it invents tool names.",
        ToolEnvelopeErrorCode.WrongTool =>
            "return the exact tool selected by ToolChoice.Named. Ensure the executor applies this "
            + "turn's Grammar, then repair or retry with bounded backpressure or change the model.",
        ToolEnvelopeErrorCode.InvalidArguments or ToolEnvelopeErrorCode.SchemaViolation =>
            "return an arguments object that satisfies the selected tool's compiled schema. Never "
            + "dispatch this response; repair or retry it with bounded backpressure, or change the "
            + "model or prompt if violations persist.",
        ToolEnvelopeErrorCode.ToolCallsRequired =>
            "return the tool_calls branch required by this turn. Ensure the executor applies this "
            + "turn's Grammar, then repair or retry with bounded backpressure or change the model.",
        ToolEnvelopeErrorCode.ToolCallsNotAllowed =>
            "return final text, or a permitted refusal, because this turn uses ToolChoice.None. "
            + "Ensure the executor applies this turn's Grammar, then repair or retry with bounded "
            + "backpressure or change the model.",
        ToolEnvelopeErrorCode.RefusalNotAllowed =>
            "return final text or a permitted tool request. Ensure the executor applies this turn's "
            + "Grammar, then repair or retry with bounded backpressure or change the model.",
        _ =>
            "return one complete envelope that follows this turn's output contract. Ensure the "
            + "executor applies ToolEnvelopeTurn.Grammar with root rule 'root'; then repair or retry "
            + "with bounded backpressure, or change the model if the violation persists.",
    };

    private static string DisplayPointer(string pointer) =>
        string.IsNullOrEmpty(pointer) ? "the response root" : $"JSON field '{pointer}'";
}

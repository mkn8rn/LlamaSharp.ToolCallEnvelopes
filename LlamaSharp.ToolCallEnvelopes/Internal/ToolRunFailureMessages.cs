namespace LlamaSharp.ToolCallEnvelopes.Internal;

internal static class ToolRunFailureMessages
{
    internal static string InvalidModelOutput(
        RunPosition position,
        int attempts,
        ToolEnvelopeError error) =>
        $"The model response remained invalid after {attempts} generation attempt(s) at "
        + $"model turn {position.TurnIndex}, attempt {position.AttemptIndex}. The last rejection "
        + $"was {error.Code} at {DisplayPointer(error.JsonPointer)}: {error.Message} The managed "
        + "runner already supplied a bounded repair prompt and did not dispatch any call from "
        + "the rejected response. Verify that the executor applies ToolEnvelopeTurn.Grammar "
        + "with root rule 'root'; then change the model, add bounded backpressure, or increase "
        + "MaxAttemptsPerTurn only when the host can afford another inference.";

    internal static string Inference(
        RunPosition position,
        Exception exception,
        string exceptionDetail) =>
        $"The model executor failed at model turn {position.TurnIndex}, attempt "
        + $"{position.AttemptIndex} while producing a response "
        + $"({exception.GetType().Name}: {exceptionDetail}). "
        + "No call from this attempt was dispatched. Verify that InferAsync applies the turn's "
        + "native prompt and grammar, yields only newly generated non-null text, and disposes "
        + "its stream cleanly; then repair the adapter, retry with backpressure, or replace the "
        + "model session.";

    internal static string Observer(
        RunPosition position,
        ToolRunEvent update,
        Exception exception,
        string exceptionDetail) =>
        $"ToolRunOptions.Observer threw {exception.GetType().Name}: {exceptionDetail} while "
        + "handling "
        + $"{update.GetType().Name} at model turn {position.TurnIndex}, attempt "
        + $"{position.AttemptIndex}. The managed pipeline stopped before emitting another event "
        + "or starting another action. Fix or remove the observer and keep required control "
        + "flow in the dispatcher or in manual orchestration.";

    internal static string ToolExecution(
        RunPosition position,
        ToolCall call,
        Exception exception,
        string exceptionDetail) =>
        $"The dispatcher threw {exception.GetType().Name}: {exceptionDetail} while executing "
        + "validated call "
        + $"{call.Index} for tool '{call.Name}' at model turn {position.TurnIndex}, attempt "
        + $"{position.AttemptIndex}. Later calls in the same batch were not started. Repair the "
        + "tool implementation, add bounded retry or backpressure around transient failures, "
        + "or handle this call in manual control flow when application-specific recovery is "
        + "required.";

    internal static string NullToolResult(RunPosition position, ToolCall call) =>
        $"The dispatcher returned null for validated call {call.Index} to tool '{call.Name}' at "
        + $"model turn {position.TurnIndex}, attempt {position.AttemptIndex}. Tool results must be "
        + "non-null text because the next prompt needs an explicit result for every completed "
        + "call. Return an empty string when an intentionally empty result is meaningful, or "
        + "return a serialized error result for the model to interpret.";

    internal static string ToolResultTooLarge(
        RunPosition position,
        ToolCall call,
        int actualCharacters,
        int maximumCharacters) =>
        $"The dispatcher returned {actualCharacters} characters for validated call {call.Index} "
        + $"to tool '{call.Name}' at model turn {position.TurnIndex}, attempt "
        + $"{position.AttemptIndex}, but this plan permits at most {maximumCharacters}. Reduce or "
        + "summarize the tool result before returning it, or deliberately raise "
        + "ToolEnvelopeLimits.MaxToolResultCharacters after checking the model context and host "
        + "memory budget.";

    internal static string ModelTurnLimit(int maximumTurns, RunPosition position) =>
        $"The managed run accepted {maximumTurns} model turn(s) without reaching a final text or "
        + $"refusal; the last accepted response was at turn {position.TurnIndex}, attempt "
        + $"{position.AttemptIndex}. Increase MaxModelTurns only when more tool rounds are "
        + "expected, choose FollowUpChoice.None to require a final response after a tool result, "
        + "or use manual control flow to apply an application-specific stopping policy.";

    private static string DisplayPointer(string pointer) =>
        string.IsNullOrEmpty(pointer) ? "the response root" : $"JSON field '{pointer}'";
}

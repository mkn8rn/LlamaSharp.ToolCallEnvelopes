# LlamaSharp adapter

`ILlamaSharpToolExecutor` is intentionally small. It receives the complete
turn and returns only newly generated response fragments.

```csharp
public sealed class LlamaSharpExecutor(
    LLamaWeights weights,
    ModelParams modelParameters) : ILlamaSharpToolExecutor
{
    public async IAsyncEnumerable<string> InferAsync(
        ToolEnvelopeTurn turn,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var context = weights.CreateContext(modelParameters);
        var executor = new InteractiveExecutor(context);
        var inference = new InferenceParams
        {
            MaxTokens = 256,
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Grammar = new Grammar(turn.Grammar, "root"),
                Temperature = 0.1f,
                TopP = 0.9f,
                Seed = 42,
            },
        };

        await foreach (var fragment in executor.InferAsync(
                           ApplyNativeTemplate(weights, turn.Prompt),
                           inference,
                           cancellationToken))
        {
            yield return fragment;
        }
    }

    private static string ApplyNativeTemplate(
        LLamaWeights weights,
        IReadOnlyList<ToolMessage> messages)
    {
        var template = new LLamaTemplate(weights, strict: true)
        {
            AddAssistant = true,
        };

        foreach (var message in messages)
            template.Add(Role(message.Role), message.Content);

        return Encoding.UTF8.GetString(template.Apply());
    }

    private static string Role(ToolMessageRole role) => role switch
    {
        ToolMessageRole.System => "system",
        ToolMessageRole.User => "user",
        ToolMessageRole.Assistant => "assistant",
        ToolMessageRole.Tool => "tool",
        _ => throw new ArgumentOutOfRangeException(
            nameof(role),
            role,
            "The adapter cannot map this ToolMessageRole to a native chat template role. "
            + "Pass messages created by the package factories, or update the adapter "
            + "explicitly when adding a newly supported role."),
    };
}
```

The example creates a context for each inference because it is easy to read.
The interface also permits a production adapter to reuse or pool contexts and
KV state. The package does not assume that an executor is stateless.

The native template is required. Concatenating role labels by hand discards
the prompt format learned by the model and makes local structured generation
less reliable. `AddAssistant = true` asks the template to end at the point
where new assistant generation begins.

The adapter owns sampling. Low-variance sampling is a useful starting point for
structured turns, but context size, token budget, temperature, seed, threads,
and GPU layers depend on the model and host. The maximum token budget must be
large enough for the bounded envelope.

Native `Grammar` objects may be cached by `turn.GrammarCacheKey`. The key
changes with the catalog, schema, limits, refusal policy, or tool choice.

The repository [demo](../.Demo/Program.cs) contains this adapter in a complete
managed and manual run against a small Qwen model.

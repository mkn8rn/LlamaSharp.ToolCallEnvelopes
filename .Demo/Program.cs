using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LLama;
using LLama.Common;
using LLama.Sampling;
using LlamaSharp.ToolCallEnvelopes;

internal static class Program
{
    private const string ModelFileName = "qwen2.5-0.5b-instruct-q4_0.gguf";
    private const string ModelUrl =
        "https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct-GGUF/resolve/main/qwen2.5-0.5b-instruct-q4_0.gguf";
    private const long ExpectedModelBytes = 428_730_208;

    public static async Task<int> Main()
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            var modelPath = await EnsureModelAsync(cancellation.Token);
            var modelParameters = new ModelParams(modelPath)
            {
                ContextSize = 2_048,
                BatchSize = 128,
                GpuLayerCount = 0,
                Threads = Math.Max(1, Environment.ProcessorCount / 2),
            };

            Console.WriteLine("Loading model...");
            using var weights = await LLamaWeights.LoadFromFileAsync(
                modelParameters,
                cancellation.Token,
                null);

            var plan = ToolEnvelopePlan.Compile([CreateWeatherTool()]);
            var executor = new LlamaSharpExecutor(weights, modelParameters);

            await RunManagedAsync(executor, plan, cancellation.Token);
            await RunManualAsync(executor, plan, cancellation.Token);
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Demo canceled.");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static async Task RunManagedAsync(
        ILlamaSharpToolExecutor executor,
        ToolEnvelopePlan plan,
        CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine("=== Managed control flow ===");

        var result = await ToolEnvelopeRunner.RunAsync(
            executor,
            plan,
            "Use the weather tool for current conditions, then answer from its result.",
            [ToolMessage.User("What is the weather in Zagreb?")],
            DispatchWeatherAsync,
            new ToolRunOptions
            {
                InitialChoice = ToolChoice.Required,
                FollowUpChoice = ToolChoice.None,
                MaxModelTurns = 2,
            },
            cancellationToken);

        switch (result)
        {
            case ToolRunResult.Completed completed:
                PrintOutcome(completed.Outcome);
                Console.WriteLine($"Executed {completed.Executions.Count} tool call(s).");
                break;

            case ToolRunResult.Failed failed:
                Console.WriteLine(
                    $"Managed run failed: {failed.Failure.Code}: {failed.Failure.Message}");
                break;
        }
    }

    private static async Task RunManualAsync(
        ILlamaSharpToolExecutor executor,
        ToolEnvelopePlan plan,
        CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine("=== Manual control flow ===");

        const string policy =
            "Use the weather tool for current conditions, then answer from its result.";
        var conversation = new List<ToolMessage>
        {
            ToolMessage.User("What is the weather in Split?"),
        };

        var toolTurn = plan.CreateTurn(policy, conversation, ToolChoice.Required);
        var toolOutcome = await InferAndParseAsync(executor, toolTurn, cancellationToken);
        var request = toolOutcome as ToolEnvelopeOutcome.ToolRequest
            ?? throw new InvalidOperationException(
                $"The manual demo created a ToolChoice.Required turn, but the model returned "
                + $"'{toolOutcome.GetType().Name}' instead of ToolRequest. Ensure the executor "
                + "attaches this turn's Grammar with root rule 'root'; then reject and retry the "
                + "response, or repair the custom inference flow before dispatching anything.");

        conversation.Add(ToolMessage.AssistantCalls(request.Calls));
        foreach (var call in request.Calls)
        {
            var result = await DispatchWeatherAsync(call, cancellationToken);
            conversation.Add(ToolMessage.ToolResult(call, result));
        }

        var answerTurn = plan.CreateTurn(policy, conversation, ToolChoice.None);
        var finalOutcome = await InferAndParseAsync(executor, answerTurn, cancellationToken);
        PrintOutcome(finalOutcome);
    }

    private static async Task<ToolEnvelopeOutcome> InferAndParseAsync(
        ILlamaSharpToolExecutor executor,
        ToolEnvelopeTurn turn,
        CancellationToken cancellationToken)
    {
        var reader = turn.CreateStreamReader();
        await foreach (var fragment in executor.InferAsync(turn, cancellationToken))
            reader.Feed(fragment);
        return reader.Complete();
    }

    private static ValueTask<string> DispatchWeatherAsync(
        ToolCall call,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(call.Name, "get_weather", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The demo dispatcher received validated call {call.Index} for tool "
                + $"'{call.Name}', but it implements only 'get_weather'. Add an explicit "
                + "dispatcher route for every tool in the compiled plan, or reject and repair "
                + "the call in manual control flow before performing a side effect.");
        }

        var city = ReadRequiredStringArgument(call, "city");
        var unit = ReadRequiredStringArgument(call, "unit");
        return ValueTask.FromResult(JsonSerializer.Serialize(new
        {
            city,
            unit,
            condition = "sunny",
            temperature = 22,
            source = "demo data",
        }));
    }

    private static string ReadRequiredStringArgument(ToolCall call, string fieldName)
    {
        if (call.Arguments.ValueKind == JsonValueKind.Object
            && call.Arguments.TryGetProperty(fieldName, out var value)
            && value.ValueKind == JsonValueKind.String
            && value.GetString() is { } text)
        {
            return text;
        }

        var observed = call.Arguments.ValueKind != JsonValueKind.Object
            ? $"an arguments value of kind {call.Arguments.ValueKind}"
            : call.Arguments.TryGetProperty(fieldName, out var invalid)
                ? $"field '{fieldName}' of kind {invalid.ValueKind}"
                : $"no field named '{fieldName}'";
        throw new InvalidOperationException(
            $"The demo dispatcher cannot execute call {call.Index} for tool '{call.Name}' because "
            + $"it expected required string field '{fieldName}', but received {observed}. Keep "
            + "the dispatcher paired with the plan that validated the call, or reject, repair, "
            + "and retry incompatible persisted input before performing a side effect.");
    }

    private static void PrintOutcome(ToolEnvelopeOutcome outcome)
    {
        switch (outcome)
        {
            case ToolEnvelopeOutcome.AssistantMessage message:
                Console.WriteLine(message.Text);
                break;
            case ToolEnvelopeOutcome.Refusal refusal:
                Console.WriteLine($"Refusal: {refusal.Reason}");
                break;
            case ToolEnvelopeOutcome.ToolRequest request:
                Console.WriteLine($"Tool request with {request.Calls.Count} call(s).");
                break;
        }
    }

    private static ToolDefinition CreateWeatherTool() =>
        ToolDefinition.Parse(
            "get_weather",
            "Gets the current weather for a city.",
            """
            {
              "type": "object",
              "properties": {
                "city": {
                  "type": "string",
                  "minLength": 1,
                  "maxLength": 64,
                  "description": "City name, for example Zagreb"
                },
                "unit": {
                  "type": "string",
                  "enum": ["celsius", "fahrenheit"],
                  "description": "Temperature unit"
                }
              },
              "required": ["city", "unit"],
              "additionalProperties": false
            }
            """);

    private static async Task<string> EnsureModelAsync(CancellationToken cancellationToken)
    {
        var modelPath = Path.Combine(AppContext.BaseDirectory, ModelFileName);
        var modelFile = new FileInfo(modelPath);
        if (modelFile.Exists && modelFile.Length == ExpectedModelBytes)
        {
            Console.WriteLine($"Using existing model: {modelPath}");
            return modelPath;
        }

        Console.WriteLine($"Downloading {ModelFileName}...");
        var temporaryPath = modelPath + ".download";
        Directory.CreateDirectory(AppContext.BaseDirectory);
        if (File.Exists(temporaryPath))
            File.Delete(temporaryPath);

        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(15),
        };
        http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("LlamaSharp.ToolCallEnvelopes.Demo", "0.2"));
        using var response = await http.GetAsync(
            ModelUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var expectedDownload = response.Content.Headers.ContentLength ?? ExpectedModelBytes;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = File.Create(temporaryPath);
        var buffer = new byte[1024 * 1024];
        var stopwatch = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        long downloaded = 0;

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            downloaded += read;
            if (stopwatch.Elapsed - lastReport >= TimeSpan.FromSeconds(2))
            {
                lastReport = stopwatch.Elapsed;
                Console.WriteLine(FormatProgress(downloaded, expectedDownload));
            }
        }

        await target.FlushAsync(cancellationToken);
        target.Close();
        var actualModelBytes = new FileInfo(temporaryPath).Length;
        if (actualModelBytes != ExpectedModelBytes)
        {
            throw new InvalidOperationException(
                $"The demo downloaded model file '{temporaryPath}', but its size is "
                + $"{actualModelBytes} bytes instead of the expected {ExpectedModelBytes} bytes. "
                + "Delete the incomplete .download file and retry with a stable connection, or "
                + "place the exact Qwen model file at the final demo model path before running again.");
        }

        File.Move(temporaryPath, modelPath, overwrite: true);
        return modelPath;
    }

    private static string FormatProgress(long downloaded, long total) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "Downloaded {0:n1} MiB / {1:n1} MiB ({2:n1}%)",
            downloaded / 1024.0 / 1024.0,
            total / 1024.0 / 1024.0,
            total == 0 ? 0 : downloaded * 100.0 / total);

    private sealed class LlamaSharpExecutor(
        LLamaWeights weights,
        ModelParams modelParameters) : ILlamaSharpToolExecutor
    {
        public async IAsyncEnumerable<string> InferAsync(
            ToolEnvelopeTurn turn,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            using var context = weights.CreateContext(modelParameters);
            var executor = new InteractiveExecutor(context);
            var grammar = new Grammar(turn.Grammar, "root");
            var inference = new InferenceParams
            {
                MaxTokens = 256,
                SamplingPipeline = new DefaultSamplingPipeline
                {
                    Grammar = grammar,
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
                Console.Write(fragment);
                yield return fragment;
            }

            Console.WriteLine();
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
                "The LlamaSharp demo adapter cannot map this ToolMessageRole to a native chat "
                + "template role. Pass messages created by the package factories, or update the "
                + "adapter explicitly when adding a newly supported role."),
        };
    }
}

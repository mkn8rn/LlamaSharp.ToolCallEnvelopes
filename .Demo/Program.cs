using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
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
    private const string WeatherToolName = "get_weather";

    public static async Task<int> Main()
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };

        try
        {
            var modelPath = await EnsureModelAsync(cts.Token);
            using var schemaDocument = CreateWeatherSchema();
            var tools = new[]
            {
                new ToolDefinition(
                    WeatherToolName,
                    "Gets the current weather for a city.",
                    schemaDocument.RootElement.Clone())
            };

            var modelParams = new ModelParams(modelPath)
            {
                ContextSize = 2048,
                BatchSize = 128,
                GpuLayerCount = 0,
                Threads = Math.Max(1, Environment.ProcessorCount / 2),
            };

            Console.WriteLine("Loading model...");
            using var weights = await LLamaWeights.LoadFromFileAsync(
                modelParams,
                cts.Token,
                null);

            await RunToolCallDemoAsync(weights, modelParams, tools, cts.Token);
            await RunRefusalDemoAsync(weights, modelParams, tools, cts.Token);
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Demo canceled.");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static async Task RunToolCallDemoAsync(
        LLamaWeights weights,
        ModelParams modelParams,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine("=== Tool-call acceptance demo ===");

        var conversation = new List<ToolAwareMessage>
        {
            ToolAwareMessage.User(
                "What is the current weather in Zagreb? Use metric units and call the weather tool.")
        };

        var firstTurn = await RunEnvelopeTurnAsync(
            weights,
            modelParams,
            "You are a local demo assistant. Call the weather tool when the user asks for weather.",
            conversation,
            tools,
            // This demo uses ForFunction to prove the tool-call path every time.
            // In a normal assistant turn where a plain text answer is also valid,
            // use ToolChoice.Auto instead so the model may choose message mode.
            ToolChoice.ForFunction(WeatherToolName),
            strictTools: true,
            allowRefusal: false,
            cancellationToken);

        RenderParsedResult(firstTurn);
        if (!firstTurn.HasToolCalls)
            throw new InvalidOperationException("The forced tool-call scenario did not produce a tool call.");

        conversation.Add(ToolAwareMessage.AssistantWithToolCalls(
            firstTurn.ToolCalls,
            firstTurn.Content));

        foreach (var call in firstTurn.ToolCalls)
        {
            var toolResult = ExecuteWeatherTool(call);
            Console.WriteLine($"Tool result for {call.Id}: {toolResult}");
            conversation.Add(ToolAwareMessage.ToolResult(call.Id, toolResult));
        }

        var finalTurn = await RunEnvelopeTurnAsync(
            weights,
            modelParams,
            "You are a local demo assistant. Answer from the supplied tool result.",
            conversation,
            tools,
            ToolChoice.None,
            strictTools: false,
            allowRefusal: false,
            cancellationToken);

        Console.WriteLine("Final answer turn:");
        RenderParsedResult(finalTurn);
    }

    private static async Task RunRefusalDemoAsync(
        LLamaWeights weights,
        ModelParams modelParams,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine("=== Refusal-capable demo ===");

        var messages = new List<ToolAwareMessage>
        {
            ToolAwareMessage.User(
                "Run the refusal-envelope demo. Decline with a short reason.")
        };

        var result = await RunEnvelopeTurnAsync(
            weights,
            modelParams,
            "You are a local demo assistant. This turn is a refusal-envelope demo; use refusal mode and do not answer in message mode.",
            messages,
            tools,
            ToolChoice.None,
            strictTools: false,
            allowRefusal: true,
            cancellationToken);

        RenderParsedResult(result);
    }

    private static async Task<ToolEnvelopeResult> RunEnvelopeTurnAsync(
        LLamaWeights weights,
        ModelParams modelParams,
        string systemPrompt,
        IReadOnlyList<ToolAwareMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        ToolChoice toolChoice,
        bool strictTools,
        bool allowRefusal,
        CancellationToken cancellationToken)
    {
        var promptHistory = LlamaSharpToolPromptBuilder.Build(
            systemPrompt,
            messages,
            tools,
            strictTools: strictTools,
            allowRefusal: allowRefusal);

        var grammar = LlamaSharpToolGrammar.Build(
            toolChoice,
            parallelCalls: false,
            tools,
            strict: strictTools,
            allowRefusal: allowRefusal);

        using var context = weights.CreateContext(modelParams);
        var executor = new InteractiveExecutor(context);
        var prompt = RenderPrompt(promptHistory);
        var output = new StringBuilder();

        var inferenceParams = new InferenceParams
        {
            MaxTokens = 256,
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Grammar = new Grammar(grammar, "root"),
                Temperature = 0.1f,
                TopP = 0.9f,
                Seed = 42,
            },
        };

        await foreach (var text in executor.InferAsync(prompt, inferenceParams, cancellationToken))
        {
            Console.Write(text);
            output.Append(text);
        }

        Console.WriteLine();
        Console.WriteLine();

        return LlamaSharpToolEnvelopeParser.Parse(output.ToString().Trim());
    }

    private static string RenderPrompt(ToolPromptHistory promptHistory)
    {
        var sb = new StringBuilder();
        foreach (var message in promptHistory.Messages)
        {
            sb.Append("### ");
            sb.AppendLine(message.Role switch
            {
                ToolPromptRole.System => "System",
                ToolPromptRole.User => "User",
                ToolPromptRole.Assistant => "Assistant",
                _ => throw new InvalidOperationException(
                    $"Unsupported prompt role '{message.Role}'.")
            });
            sb.AppendLine(message.Content);
            sb.AppendLine();
        }

        sb.AppendLine("### Assistant");
        return sb.ToString();
    }

    private static void RenderParsedResult(ToolEnvelopeResult result)
    {
        switch (result.Mode)
        {
            case LlamaSharpToolEnvelopeParser.ToolCallsMode:
                foreach (var call in result.ToolCalls)
                {
                    Console.WriteLine(
                        FormattableString.Invariant(
                            $"Parsed tool call: id={call.Id}, name={call.Name}, args={call.ArgumentsJson}"));
                }
                break;

            case LlamaSharpToolEnvelopeParser.RefusalMode:
                Console.WriteLine($"Parsed refusal: {result.Refusal}");
                break;

            case LlamaSharpToolEnvelopeParser.MessageMode:
                Console.WriteLine($"Parsed message: {result.Content}");
                break;

            default:
                throw new InvalidOperationException($"Unexpected envelope mode '{result.Mode}'.");
        }
    }

    private static string ExecuteWeatherTool(ToolCall call)
    {
        if (!string.Equals(call.Name, WeatherToolName, StringComparison.Ordinal))
            throw new InvalidOperationException($"Unknown tool '{call.Name}'.");

        using var argsDocument = JsonDocument.Parse(call.ArgumentsJson);
        var root = argsDocument.RootElement;
        var city = root.GetProperty("city").GetString();
        var unit = root.TryGetProperty("unit", out var unitElement)
            ? unitElement.GetString()
            : "celsius";

        return JsonSerializer.Serialize(new
        {
            city,
            unit,
            condition = "sunny",
            temperature = 22,
            source = "hardcoded demo tool",
        });
    }

    private static JsonDocument CreateWeatherSchema() =>
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "city": {
                  "type": "string",
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

        if (modelFile.Exists)
        {
            Console.WriteLine(
                FormattableString.Invariant(
                    $"Existing model file has {modelFile.Length} bytes; expected {ExpectedModelBytes}. Re-downloading."));
        }

        Console.WriteLine($"Downloading {ModelFileName} to assembly directory...");
        Console.WriteLine(ModelUrl);

        var tempPath = modelPath + ".download";
        Directory.CreateDirectory(AppContext.BaseDirectory);
        if (File.Exists(tempPath))
            File.Delete(tempPath);

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(
            "LlamaSharp.ToolCallEnvelopes.Demo",
            "0.1"));
        http.Timeout = Timeout.InfiniteTimeSpan;

        using var response = await http.GetAsync(
            ModelUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? ExpectedModelBytes;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = File.Create(tempPath);

        var buffer = new byte[1024 * 1024];
        long downloaded = 0;
        var stopwatch = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;

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
                Console.WriteLine(FormatProgress(downloaded, totalBytes));
            }
        }

        await target.FlushAsync(cancellationToken);
        target.Close();

        var downloadedFile = new FileInfo(tempPath);
        if (downloadedFile.Length != ExpectedModelBytes)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant(
                    $"Downloaded model has {downloadedFile.Length} bytes; expected {ExpectedModelBytes}."));
        }

        File.Move(tempPath, modelPath, overwrite: true);
        Console.WriteLine($"Downloaded model: {modelPath}");
        return modelPath;
    }

    private static string FormatProgress(long downloaded, long totalBytes)
    {
        var percent = totalBytes > 0
            ? downloaded * 100.0 / totalBytes
            : 0;

        return string.Format(
            CultureInfo.InvariantCulture,
            "Downloaded {0:n1} MiB / {1:n1} MiB ({2:n1}%)",
            downloaded / 1024.0 / 1024.0,
            totalBytes / 1024.0 / 1024.0,
            percent);
    }
}

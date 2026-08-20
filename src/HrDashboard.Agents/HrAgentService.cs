using HrDashboard.Agents.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace HrDashboard.Agents;

public sealed class HrAgentService : IHrAgentService, IAsyncDisposable
{
    private readonly IChatClient _chatClient;
    private readonly string _mcpServerEndpoint;
    private readonly ILogger<HrAgentService> _logger;
    private McpClient? _mcpClient;
    private IList<AITool> _tools = [];
    private bool _initialized;

    private const int MaxIterations = 20;

    private const string SystemPrompt = """
        You are an AI HR Analytics Assistant connected to an Oracle HR database via MCP tools.

        Available tools give you access to:
        - Employee salary data by department
        - Department headcounts
        - Job salary ranges
        - Custom SQL queries (SELECT only)

        When a user asks an HR analytics question:
        1. Call the most appropriate tool(s) to fetch data.
        2. Analyze the results.
        3. Return a JSON array of HrMetricRow objects as part of your response.

        ALWAYS include in your final response a JSON array like:
        [{"label":"Executive","value":17000.0,"category":"AvgSalary"},...]

        The JSON array must appear directly in the response text (not in a code block).
        After the JSON, add a one-sentence natural language summary.
        """;

    public HrAgentService(IChatClient chatClient, string mcpServerEndpoint, ILogger<HrAgentService> logger)
    {
        _chatClient        = chatClient;
        _mcpServerEndpoint = mcpServerEndpoint;
        _logger            = logger;
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;

        _logger.LogInformation("Connecting to HrDashboard.McpServer at {Endpoint}", _mcpServerEndpoint);

        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(_mcpServerEndpoint)
        });

        _mcpClient = await McpClient.CreateAsync(transport, cancellationToken: ct);
        _tools     = (await _mcpClient.ListToolsAsync(cancellationToken: ct)).Cast<AITool>().ToList();
        _initialized = true;

        _logger.LogInformation("Connected — {Count} HR tools available: {Names}",
            _tools.Count,
            string.Join(", ", _tools.Select(t => t.Name)));
    }

    public async Task<(string RawText, IReadOnlyList<HrMetricRow> Metrics)> AskAsync(
        string prompt,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        _logger.LogInformation("HR agent prompt: {Prompt}", prompt);

        var options = new ChatOptions { Tools = [.. _tools] };
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User,   prompt)
        };

        for (int i = 0; i < MaxIterations; i++)
        {
            var response = await _chatClient.GetResponseAsync(messages, options, ct);

            foreach (var msg in response.Messages)
                messages.Add(msg);

            var calls = response.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .ToList();

            if (calls.Count == 0)
            {
                var raw = response.Text ?? string.Empty;
                _logger.LogInformation("Agent completed in {Iterations} iteration(s)", i + 1);
                var metrics = HrMetricParser.Parse(raw);
                return (raw, metrics);
            }

            foreach (var call in calls)
            {
                _logger.LogDebug("Tool call: {Tool}({Args})", call.Name,
                    string.Join(", ", (call.Arguments ?? new Dictionary<string, object?>()).Select(kv => $"{kv.Key}={kv.Value}")));

                var result = await InvokeToolAsync(call, ct);

                messages.Add(new ChatMessage(ChatRole.Tool,
                    [new FunctionResultContent(call.CallId, result)]));
            }
        }

        _logger.LogWarning("Agent hit iteration limit for prompt: {Prompt}", prompt);
        return ("[Agent reached iteration limit — rephrase your query]", []);
    }

    private List<ChatMessage> BuildMessages(
        IEnumerable<(MessageRole Role, string Content)> history,
        string prompt)
    {
        var messages = new List<ChatMessage> { new(ChatRole.System, SystemPrompt) };
        foreach (var (role, content) in history)
        {
            var chatRole = role == MessageRole.User ? ChatRole.User : ChatRole.Assistant;
            messages.Add(new ChatMessage(chatRole, content));
        }
        messages.Add(new ChatMessage(ChatRole.User, prompt));
        return messages;
    }

    public async IAsyncEnumerable<string> AskStreamAsync(
        IEnumerable<(MessageRole Role, string Content)> history,
        string prompt,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        _logger.LogInformation("HR agent streaming prompt: {Prompt}", prompt);

        var toolOptions = new ChatOptions { Tools = [.. _tools] };
        var messages = BuildMessages(history, prompt);
        bool toolsWereUsed = false;

        // Phase 1: tool-use loop (non-streaming) to gather Oracle HR data.
        // We do NOT add the final no-tool-call response to messages; Phase 2 streams it.
        for (int i = 0; i < MaxIterations; i++)
        {
            var response = await _chatClient.GetResponseAsync(messages, toolOptions, ct);

            var calls = response.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .ToList();

            if (calls.Count == 0)
            {
                if (!toolsWereUsed)
                {
                    // Model answered without any tool calls — yield text directly (no extra API call)
                    _logger.LogInformation("Agent streaming (no tools) completed in 1 round");
                    yield return response.Text ?? string.Empty;
                    yield break;
                }

                // Tool data is in messages; fall through to Phase 2 for streaming final answer
                _logger.LogInformation("Agent tool-use loop done after {Rounds} round(s), streaming final answer", i);
                break;
            }

            toolsWereUsed = true;

            if (i == MaxIterations - 1)
            {
                _logger.LogWarning("Agent streaming hit iteration limit for prompt: {Prompt}", prompt);
                yield return "[Agent reached iteration limit — rephrase your query]";
                yield break;
            }

            // Add tool-call messages and results; final answer is never added here
            foreach (var msg in response.Messages)
                messages.Add(msg);

            foreach (var call in calls)
            {
                _logger.LogDebug("Tool call: {Tool}", call.Name);
                var result = await InvokeToolAsync(call, ct);
                messages.Add(new ChatMessage(ChatRole.Tool,
                    [new FunctionResultContent(call.CallId, result)]));
            }
        }

        // Phase 2: stream the final summarization over the accumulated tool context
        await foreach (var update in _chatClient.GetStreamingResponseAsync(messages, new ChatOptions(), ct))
        {
            if (!string.IsNullOrEmpty(update.Text))
                yield return update.Text;
        }
    }

    private async Task<object?> InvokeToolAsync(FunctionCallContent call, CancellationToken ct)
    {
        var fn = _tools.OfType<AIFunction>()
            .FirstOrDefault(t => string.Equals(t.Name, call.Name, StringComparison.OrdinalIgnoreCase));

        if (fn is null)
        {
            _logger.LogWarning("Tool not found: {Name}", call.Name);
            return $"[Tool '{call.Name}' not found]";
        }

        try
        {
            var args = call.Arguments is not null
                ? new AIFunctionArguments(call.Arguments)
                : new AIFunctionArguments(new Dictionary<string, object?>());
        return await fn.InvokeAsync(args, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tool {Name} threw an exception", call.Name);
            return $"[Tool error: {ex.Message}]";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_mcpClient is not null)
            await _mcpClient.DisposeAsync();
    }
}

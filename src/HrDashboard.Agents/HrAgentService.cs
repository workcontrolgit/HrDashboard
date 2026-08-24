using System.Diagnostics;
using System.Text;
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

    public PendingColumnOptions? LastPendingColumnOptions { get; private set; }

    private const int MaxIterations = 20;

    private const string SystemPrompt = """
        You are an AI HR Analytics Assistant connected to an HR database via MCP tools.

        Available tools give you access to:
        - Employee salary data by department
        - Department headcounts
        - Job salary ranges
        - Schema discovery (list tables, describe table columns)
        - Custom SQL queries (SELECT only)

        When a user asks an HR analytics question, first decide which of these three
        shapes it is, then follow that shape's rules:

        A. A LISTING of raw records (e.g. "list employees", "show me all managers",
           "who works in Sales") — the user wants individual rows, not a computed number.
        B. A single computed METRIC or aggregate (e.g. "average salary by department",
           "headcount per department", "job salary ranges") — already has a fixed,
           self-explanatory shape: one label/value/category per group.
        C. Not about HR data at all (e.g. stock prices, the weather) — no tool applies.

        ListTables and DescribeTable are schema-discovery tools, free to chain in any
        shape — call them first, as many times as needed, whenever you don't already
        know the exact table or column names you need.

        Shape A — LISTING requests:
        1. Call DescribeTable on the relevant table first, to learn its real column
           names. Never guess, invent, or assume column names.
        2. Then STOP. Do not call RunHrQuery yet. Ask the user, in plain natural
           language, which columns they'd like to see — mention a few of the most
           useful ones as a suggested default, and note that other real columns are
           also available, using only the exact names DescribeTable returned. This
           message is a question, not an answer: do not include a JSON metrics array
           in it.
        3. Once the user replies (in their next message) saying which columns they
           want, call RunHrQuery selecting exactly those columns and report only what
           it actually returned, following the JSON contract below. Set
           "chartable":false and include "labelName"/"valueName"/"categoryName" as
           described below, since a listing is a set of records, not a metric.

        NEVER answer a listing question using only ListTables/DescribeTable results
        without first asking about columns as described above, and NEVER invent,
        guess, or use example/placeholder values (like "John Smith" or "Jane Doe") in
        place of real data.

        Shape B — METRIC/aggregate requests:
        Call exactly ONE tool — the single most specific one whose description matches
        the question (GetTopEarnersByDepartment, GetSalaryBreakdownByDepartment,
        GetDeptHeadcount, GetJobSalaryRanges, or RunHrQuery as a last resort). Each
        tool call costs several seconds of real latency, so calling more than one tool
        for a question a single tool already answers in full is a mistake, not extra
        thoroughness. Once a tool's result answers the question, stop — do not call
        another tool to double-check, cross-reference, or re-derive the same numbers a
        different way. Only call a second tool if the first tool's result is genuinely
        missing something the user asked for. There is nothing to negotiate about
        columns for this shape — report the result directly using the JSON contract
        below.

        Shape C — no matching tool:
        Say so honestly, in plain natural language. Do not call RunHrQuery or any
        other tool against unrelated intent, and do not fabricate an answer.

        If the question is purely conversational and has no HR data to report at all
        (e.g. "who are you", "what can you do", a greeting), also just answer in plain
        natural language — do not invent a row or force a placeholder value just to
        satisfy the JSON format below.

        --- JSON contract for final data answers (Shape A step 3, and Shape B) ---

        Whenever your answer reports actual HR data — a metric, a computed value, or a
        list of records from a tool result — include a JSON array in your final
        response like:
        [{"label":"Executive","value":17000.0,"category":"AvgSalary"},...]

        Each object may also include "chartable":false when the result is a plain
        listing with no meaningful single numeric value per row (e.g. "list
        employees", where each row is a record, not a metric) — omit "chartable" (it
        defaults to true) for genuine metrics like averages, headcounts, or ranges,
        where a bar/line/donut chart makes sense. When "chartable" is false, still set
        "label" to something identifying the row (e.g. an employee's name) and "value"
        to any real numeric field from that row (e.g. salary) rather than a
        placeholder — the data still needs to populate a data table even though no
        chart is drawn from it.

        When "chartable" is false, also include "labelName", "valueName", and (if
        used) "categoryName" giving the real field names those columns hold (e.g.
        "labelName":"Employee Name", "valueName":"Salary", "categoryName":
        "Department") — the results table shows these as its column headers instead
        of the generic "Label"/"Value"/"Category" so a listing reads like real data,
        not abstract metric axes. Repeat the same three names on every row in the
        array. Omit them entirely for genuine metrics (chartable true or absent),
        where "Label"/"Value"/"Category" are already meaningful.

        "labelName"/"valueName"/"categoryName" are additional column-header
        overrides, never a replacement for "label"/"value"/"category" — every row
        must still include real "label" and "value" data (e.g. the actual employee
        name and salary) regardless of whether you also include the header-override
        fields.

        "label" and "category" are always JSON strings, in quotes — even when the
        value looks numeric (e.g. a department ID). Prefer a human-readable name over
        a raw ID when one is available (e.g. the department's name rather than its
        numeric ID).

        "value" is always a JSON number, never null, on any row you do include.

        The JSON array must appear directly in the response text (not in a code
        block). After the JSON, add a one-sentence natural language summary.

        Your final data-answer response must contain ONLY the JSON array followed by
        the one-sentence summary — nothing else. Never repeat, quote, or paraphrase
        the tool call you made or the raw tool result payload; that data is
        scaffolding for you, not something to show the user.

        Never write narration about calling a tool — not in this turn, not in any
        earlier turn. Do not write sentences like "Calling X tool..." or "I'll check
        Y..."; simply invoke the tool directly. Any text you write, in any turn, is
        potentially shown to the user, so it must always be one of: silence (while
        only calling tools), the Shape A column-choice question, a Shape C or
        conversational plain-language answer, or the final JSON array plus
        one-sentence summary — never a description of what you're doing.
        """;

    public HrAgentService(IChatClient chatClient, string mcpServerEndpoint, ILogger<HrAgentService> logger)
    {
        _chatClient        = chatClient;
        _mcpServerEndpoint = mcpServerEndpoint;
        _logger            = logger;
    }

    /// <summary>Test-only constructor — bypasses MCP HTTP initialization.</summary>
    internal HrAgentService(
        IChatClient chatClient,
        IList<AITool> tools,
        ILogger<HrAgentService> logger)
    {
        _chatClient        = chatClient;
        _mcpServerEndpoint = string.Empty;
        _logger            = logger;
        _tools             = tools;
        _initialized       = true;
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

    public async Task<(string RawText, IReadOnlyList<HrMetricRow> Metrics, PendingColumnOptions? PendingColumns)> AskAsync(
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
        var tracker = new ToolCallTracker();

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
                return (raw, metrics, tracker.Classify(raw));
            }

            foreach (var call in calls)
            {
                _logger.LogDebug("Tool call: {Tool}({Args})", call.Name,
                    string.Join(", ", (call.Arguments ?? new Dictionary<string, object?>()).Select(kv => $"{kv.Key}={kv.Value}")));

                var result = await InvokeToolAsync(call, ct);
                tracker.Observe(call, result);

                messages.Add(new ChatMessage(ChatRole.Tool,
                    [new FunctionResultContent(call.CallId, result)]));
            }
        }

        _logger.LogWarning("Agent hit iteration limit for prompt: {Prompt}", prompt);
        return ("[Agent reached iteration limit — rephrase your query]", [], null);
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

        LastPendingColumnOptions = null;
        _logger.LogInformation("HR agent streaming prompt: {Prompt}", prompt);

        var totalStopwatch = Stopwatch.StartNew();
        var toolOptions = new ChatOptions { Tools = [.. _tools] };
        var messages = BuildMessages(history, prompt);
        bool toolsWereUsed = false;
        var tracker = new ToolCallTracker();

        // Phase 1: tool-use loop (non-streaming) to gather Oracle HR data.
        // We do NOT add the final no-tool-call response to messages; Phase 2 streams it.
        for (int i = 0; i < MaxIterations; i++)
        {
            var roundStopwatch = Stopwatch.StartNew();
            var response = await _chatClient.GetResponseAsync(messages, toolOptions, ct);
            _logger.LogInformation("Phase 1 round {Round}: model call took {ElapsedMs}ms", i, roundStopwatch.ElapsedMilliseconds);

            var calls = response.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .ToList();

            if (calls.Count == 0)
            {
                if (!toolsWereUsed)
                {
                    // Model answered without any tool calls — yield text directly (no extra API call)
                    _logger.LogInformation("Agent streaming (no tools) completed in 1 round, {ElapsedMs}ms total", totalStopwatch.ElapsedMilliseconds);
                    yield return response.Text ?? string.Empty;
                    yield break;
                }

                // Tool data is in messages; fall through to Phase 2 for streaming final answer
                _logger.LogInformation(
                    "Agent tool-use loop done after {Rounds} round(s) in {ElapsedMs}ms, streaming final answer",
                    i, totalStopwatch.ElapsedMilliseconds);
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
                var toolStopwatch = Stopwatch.StartNew();
                var result = await InvokeToolAsync(call, ct);
                tracker.Observe(call, result);
                _logger.LogInformation("Tool call {Tool} took {ElapsedMs}ms", call.Name, toolStopwatch.ElapsedMilliseconds);
                messages.Add(new ChatMessage(ChatRole.Tool,
                    [new FunctionResultContent(call.CallId, result)]));
            }
        }

        // Phase 2: stream the final summarization over the accumulated tool context
        var phase2Stopwatch = Stopwatch.StartNew();
        var chunkCount = 0;
        var fullTextBuilder = new StringBuilder();
        await foreach (var update in _chatClient.GetStreamingResponseAsync(messages, new ChatOptions(), ct))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                chunkCount++;
                fullTextBuilder.Append(update.Text);
                yield return update.Text;
            }
        }

        LastPendingColumnOptions = tracker.Classify(fullTextBuilder.ToString());

        _logger.LogInformation(
            "Agent streaming final answer complete: {ChunkCount} chunk(s), phase 2 took {Phase2Ms}ms, {TotalMs}ms total",
            chunkCount, phase2Stopwatch.ElapsedMilliseconds, totalStopwatch.ElapsedMilliseconds);
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

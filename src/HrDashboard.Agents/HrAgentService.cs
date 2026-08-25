using System.Diagnostics;
using HrDashboard.Agents.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace HrDashboard.Agents;

public sealed class HrAgentService : IHrAgentService, IAsyncDisposable
{
    private readonly IChatClient _chatClient;
    private readonly string _mcpServerEndpoint;
    private readonly string _providerName;
    private readonly string _modelName;
    private readonly ILogger<HrAgentService> _logger;
    private McpClient? _mcpClient;
    private IList<AITool> _tools = [];
    private bool _initialized;

    public PendingColumnOptions? LastPendingColumnOptions { get; private set; }

    public HrDataSet? LastDataSet { get; private set; }

    public TurnUsageInfo? LastTurnUsage { get; private set; }

    private const int MaxIterations = 20;

    // Ollama's default runtime context window (4096 tokens for gemma4:12b here, confirmed via
    // GET /api/ps) is well below what this SystemPrompt plus the MCP tool schemas plus a couple
    // of turns of history needs — a second turn (e.g. the "Show columns: ..." follow-up to the
    // Shape A clarifying question) silently exceeded it, producing a fast, empty, zero-tool-call
    // response instead of an error. Cloud providers (Nvidia, Azure OpenAI) already run with a much
    // larger context window, so this only needs to apply to Ollama.
    private const int OllamaNumCtx = 8192;

    private ChatOptions ApplyProviderTuning(ChatOptions options)
    {
        if (string.Equals(_providerName, "Ollama", StringComparison.OrdinalIgnoreCase))
            options.AdditionalProperties = new AdditionalPropertiesDictionary { ["num_ctx"] = OllamaNumCtx };
        return options;
    }

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
           self-explanatory shape: one row per group, with a category column and a
           numeric value column.
        C. Not about HR data at all (e.g. stock prices, the weather) — no tool applies.

        ListTables and DescribeTable are schema-discovery tools, free to chain in any
        shape — call them first, as many times as needed, whenever you don't already
        know the exact table or column names you need.

        Two views already do this joining for you — prefer them over a manual join
        whenever they cover what the user asked for:
        - EMPLOYEES_ENRICHED — every EMPLOYEES column, plus DEPARTMENT_NAME,
          JOB_TITLE/JOB_MIN_SALARY/JOB_MAX_SALARY, MANAGER_NAME, and CITY/
          STATE_PROVINCE, all pre-resolved. Use this instead of EMPLOYEES for almost
          any employee listing — it covers department, job, manager, and location
          in one table, no join needed.
        - DEPARTMENTS_ENRICHED — every DEPARTMENTS column, plus MANAGER_NAME and
          CITY/STATE_PROVINCE/COUNTRY_ID, pre-resolved. Use this instead of
          DEPARTMENTS for any department listing that mentions the manager or
          location.
        DescribeTable one of these views exactly like a table — one DescribeTable
        call, one SELECT, no join required, since the relationship is already built
        into the view.

        This HR schema also has these underlying foreign-key relationships, for the
        rarer case where a request needs a raw table the views don't cover:
        - EMPLOYEES.MANAGER_ID and DEPARTMENTS.MANAGER_ID both reference
          EMPLOYEES.EMPLOYEE_ID — the manager's name (join back to EMPLOYEES using
          its FIRST_NAME/LAST_NAME columns).
        - EMPLOYEES.DEPARTMENT_ID references DEPARTMENTS.DEPARTMENT_ID — the
          department's name (DEPARTMENTS.DEPARTMENT_NAME).
        - EMPLOYEES.JOB_ID references JOBS.JOB_ID — the job title (JOBS.JOB_TITLE).
        - DEPARTMENTS.LOCATION_ID references LOCATIONS.LOCATION_ID — the location
          (e.g. LOCATIONS.CITY).
        If neither enriched view covers the request, DescribeTable the related
        table too if you haven't already, then use RunHrQuery with an explicit SQL
        JOIN to include the human-readable name instead of (or alongside) the raw
        ID — RunHrQuery accepts any SELECT statement, including joins across
        EMPLOYEES, DEPARTMENTS, JOBS, and LOCATIONS. Never show a bare foreign-key
        ID number when the related table or enriched view that resolves it is
        available.

        Shape A — LISTING requests:
        1. Call DescribeTable on the relevant table first, to learn its real column
           names. Never guess, invent, or assume column names. If the request
           touches a foreign-key column covered above, also DescribeTable the
           related table.
        2. Then STOP. Do not call RunHrQuery yet. Ask the user, in plain natural
           language, which columns they'd like to see — mention a few of the most
           useful ones as a suggested default, and note that other real columns are
           also available. Describe a foreign-key column by what it represents
           (e.g. "Manager Name", "Department Name", "Job Title"), never by its raw
           column name like "MANAGER_ID" — the user is choosing what to see, not
           raw schema. This message is a question, not an answer: do not include a
           dataset JSON object in it.
        3. Once the user replies (in their next message) saying which columns they
           want, call RunHrQuery selecting exactly those columns — joining in the
           related table for any foreign-key column, per the relationships above.
           Alias every selected column with its real, human-readable name directly
           in the SQL (e.g. SELECT department_name AS "Department Name", ...) —
           RunHrQuery's own result becomes the listing's data, so these aliases ARE
           the column headers the user sees. Then report using the lightweight
           listing contract below, never the full dataset contract — you never
           re-type the rows RunHrQuery already returned.

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
        columns for this shape — report the result directly using the dataset
        contract below. If you end up using RunHrQuery for this (the "last resort"
        case above), alias its SELECT columns with human-readable names and use the
        lightweight listing contract instead — the same rule as Shape A step 3,
        since RunHrQuery's own result becomes the answer's data either way.

        Shape C — no matching tool:
        Say so honestly, in plain natural language. Do not call RunHrQuery or any
        other tool against unrelated intent, and do not fabricate an answer.

        If the question is purely conversational and has no HR data to report at all
        (e.g. "who are you", "what can you do", a greeting), also just answer in plain
        natural language — do not invent a row or force a placeholder value just to
        satisfy the dataset format below.

        --- Lightweight listing contract, for a RunHrQuery result (Shape A step 3, and
        Shape B's RunHrQuery fallback) ---

        RunHrQuery's own result IS the listing's data — you never re-type it. Once
        RunHrQuery has returned rows, include a small JSON object in your final
        response instead of the full dataset contract below:
        {"datasetMeta":{"title":"Employees by Department","chartRecommendation":null}}

        "title" is a short human-readable heading for the listing (e.g. "Employees
        in IT", "Departments and Managers").

        Include "chartRecommendation" only when one of the columns you selected in
        your SELECT is genuinely numeric and worth charting against another selected
        column as its category (e.g. salary against employee name — not an ID or a
        date). When you do, it must reference the exact column aliases you used in
        SELECT: {"xAxisColumn":"<a real column alias>","yAxisColumn":"<a real
        numeric column alias>","reason":"<one short phrase>"}. Otherwise set it to
        null.

        The datasetMeta JSON object must appear directly in the response text (not
        in a code block). After it, add a one-sentence natural language summary of
        what the query returned (e.g. how many rows, a notable value) — never
        restate the actual row data; the user already sees it in the results grid.

        If RunHrQuery returned zero rows, skip datasetMeta entirely — just say so in
        plain language (e.g. "No employees match that department.").

        --- Dataset contract for final data answers (Shape B's curated tools — GetTopEarnersByDepartment,
        GetSalaryBreakdownByDepartment, GetDeptHeadcount, GetJobSalaryRanges) ---

        Whenever your answer reports actual HR data — a metric, a computed value, or a
        list of records from a tool result — include a JSON object in your final
        response like:
        {"dataset":{"title":"Average Salary by Department","columns":[{"name":"Department","type":"string"},{"name":"AvgSalary","type":"number"}],"rows":[{"Department":"Executive","AvgSalary":17000.0},{"Department":"IT","AvgSalary":7500.0}]}}

        "title" is a short human-readable heading for the data (e.g. "Average Salary
        by Department", "Top 10 Highest-Paid Employees").

        "columns" lists every field that appears in each row, in the order they
        should be displayed, each with:
        - "name": the exact field name used as the key in every row object below.
          Use real, human-readable names (e.g. "Department", "Employee Name",
          "Salary") — never generic placeholders like "Label"/"Value"/"Category".
        - "type": either "number" (for values that should be treated as numeric — the
          only type a chart's Y-axis can use) or "string" (the default — use for
          names, categories, or any other text).

        "rows" is one object per record, with one key per column "name", holding the
        real value for that row — a JSON number for "number" columns, a JSON string
        for "string" columns. Never invent, guess, or use example/placeholder values
        (like "John Smith" or "Jane Doe") in place of real data.

        Include "chartRecommendation" only when the data has both a natural
        categorical axis and a genuinely numeric column worth charting (e.g. an
        amount, a count, a salary — not an ID or a date). When you do, it must be:
        {"xAxisColumn":"<a real column name from columns above>","yAxisColumn":"<a
        real column name whose type is \"number\">","reason":"<one short phrase>"}
        Omit "chartRecommendation" entirely for a plain listing with no single
        meaningful numeric column to chart (e.g. a raw employee roster with many
        unrelated fields), or when no column is genuinely numeric.

        The dataset JSON object must appear directly in the response text (not in a
        code block). After it, add a one-sentence natural language summary.

        Your final data-answer response must contain ONLY the dataset JSON object
        followed by the one-sentence summary — nothing else. Never repeat, quote, or
        paraphrase the tool call you made or the raw tool result payload; that data is
        scaffolding for you, not something to show the user.

        Never write narration about calling a tool — not in this turn, not in any
        earlier turn. Do not write sentences like "Calling X tool..." or "I'll check
        Y..."; simply invoke the tool directly. Any text you write, in any turn, is
        potentially shown to the user, so it must always be one of: silence (while
        only calling tools), the Shape A column-choice question, a Shape C or
        conversational plain-language answer, or a final answer following whichever
        contract applies (the lightweight datasetMeta object, or the full dataset
        object) plus its one-sentence summary — never a description of what you're
        doing.
        """;

    public HrAgentService(
        IChatClient chatClient,
        string mcpServerEndpoint,
        string providerName,
        string modelName,
        ILogger<HrAgentService> logger)
    {
        _chatClient        = chatClient;
        _mcpServerEndpoint = mcpServerEndpoint;
        _providerName      = providerName;
        _modelName         = modelName;
        _logger            = logger;
    }

    /// <summary>Test-only constructor — bypasses MCP HTTP initialization.</summary>
    internal HrAgentService(
        IChatClient chatClient,
        IList<AITool> tools,
        ILogger<HrAgentService> logger,
        string providerName = "TestProvider",
        string modelName = "test-model")
    {
        _chatClient        = chatClient;
        _mcpServerEndpoint = string.Empty;
        _providerName      = providerName;
        _modelName         = modelName;
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
        LastDataSet = null;

        _logger.LogInformation("HR agent prompt: {Prompt}", prompt);

        // meta/llama-3.1-8b-instruct's chat template hard-rejects a response containing more
        // than one tool call ("Failed to apply prompt template: ... This model only supports
        // single tool-calls at once!"), returned as an HTTP 500 that aborts the whole turn.
        // The SystemPrompt's "free to chain" schema-discovery language can otherwise tempt the
        // model into bundling a DescribeTable call together with the analytics tool call in one
        // response. AllowMultipleToolCalls=false only limits calls WITHIN one response — the
        // loop below still lets the model call schema tools across as many separate rounds as
        // it wants before its one-tool-per-round analytics call.
        var options = ApplyProviderTuning(new ChatOptions { Tools = [.. _tools], AllowMultipleToolCalls = false });
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
                HrDataSetParser.TryParse(raw, out var dataSet);
                LastDataSet = dataSet;
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
        LastTurnUsage = null;
        LastDataSet = null;
        _logger.LogInformation("HR agent streaming prompt: {Prompt}", prompt);

        var totalStopwatch = Stopwatch.StartNew();
        // AllowMultipleToolCalls=false — see the identical setting in AskAsync for why.
        var toolOptions = ApplyProviderTuning(new ChatOptions { Tools = [.. _tools], AllowMultipleToolCalls = false });
        var messages = BuildMessages(history, prompt);
        bool toolsWereUsed = false;
        var tracker = new ToolCallTracker();

        long totalInputTokens = 0, totalOutputTokens = 0, totalTokens = 0;
        bool anyUsageSeen = false;

        TurnUsageInfo? FinalizeUsage() =>
            anyUsageSeen ? new TurnUsageInfo(_providerName, _modelName, totalInputTokens, totalOutputTokens, totalTokens) : null;

        // Tool-use loop. The round that finally comes back with no tool calls IS the final
        // answer — we used to discard its text and re-issue a second, separate streaming call
        // over the identical accumulated context just to "stream" it, which doubled the LLM
        // latency of every tool-using turn for no benefit (bug: perceived response time was
        // ~2x a single generation). We now yield that round's text directly instead.
        for (int i = 0; i < MaxIterations; i++)
        {
            var roundStopwatch = Stopwatch.StartNew();
            var response = await _chatClient.GetResponseAsync(messages, toolOptions, ct);
            _logger.LogInformation("Round {Round}: model call took {ElapsedMs}ms", i, roundStopwatch.ElapsedMilliseconds);

            if (response.Usage is { } roundUsage)
            {
                anyUsageSeen = true;
                totalInputTokens  += roundUsage.InputTokenCount ?? 0;
                totalOutputTokens += roundUsage.OutputTokenCount ?? 0;
                // Some providers populate input/output but not total — fall back to their
                // sum rather than letting this update contribute 0 to the running total.
                totalTokens       += roundUsage.TotalTokenCount ?? ((roundUsage.InputTokenCount ?? 0) + (roundUsage.OutputTokenCount ?? 0));
            }

            var calls = response.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .ToList();

            if (calls.Count == 0)
            {
                var finalText = response.Text ?? string.Empty;
                _logger.LogInformation(
                    "Agent streaming completed in {Rounds} round(s), {ElapsedMs}ms total", i + 1, totalStopwatch.ElapsedMilliseconds);

                if (toolsWereUsed)
                {
                    LastPendingColumnOptions = tracker.Classify(finalText);
                    LastDataSet = ResolveDataSet(tracker, finalText);
                }
                LastTurnUsage = FinalizeUsage();
                yield return finalText;
                yield break;
            }

            toolsWereUsed = true;

            if (i == MaxIterations - 1)
            {
                _logger.LogWarning("Agent streaming hit iteration limit for prompt: {Prompt}", prompt);
                LastTurnUsage = FinalizeUsage();
                yield return "[Agent reached iteration limit — rephrase your query]";
                yield break;
            }

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
    }

    /// <summary>
    /// Prefers building the HrDataSet directly from RunHrQuery's own raw result over parsing
    /// it back out of the model's generated text — the model only supplied a title/chart
    /// recommendation (the "datasetMeta" contract), never the row data itself, so this keeps
    /// token cost flat and removes output-truncation risk regardless of row count. Falls
    /// back to the full "dataset" contract (Shape B's curated tools, where the model still
    /// authors the whole payload since their results are small and bounded) whenever the raw
    /// result isn't there or isn't a usable row array — including a genuinely empty result,
    /// where the model's own plain-language "no rows matched" answer is left to stand as-is.
    /// </summary>
    private static HrDataSet? ResolveDataSet(ToolCallTracker tracker, string finalText)
    {
        if (string.Equals(tracker.LastDataToolName, "RunHrQuery", StringComparison.OrdinalIgnoreCase)
            && tracker.LastDataToolResult is not null)
        {
            HrDataSetParser.TryParseMeta(finalText, out var meta);
            if (HrDataSetParser.TryBuildFromRawResult(tracker.LastDataToolResult, meta, out var rawDataSet))
                return rawDataSet;
        }

        HrDataSetParser.TryParse(finalText, out var parsedDataSet);
        return parsedDataSet;
    }

    public async Task<IReadOnlyList<TableOverview>> GetSchemaOverviewAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        var listTablesFn = _tools.OfType<AIFunction>()
            .FirstOrDefault(t => string.Equals(t.Name, "ListTables", StringComparison.OrdinalIgnoreCase));
        if (listTablesFn is null) return [];

        var listResult = await listTablesFn.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>()), ct);
        var listJson = SchemaJsonParser.ExtractStringResult(listResult);
        if (listJson is null || !SchemaJsonParser.TryParseRowValues(listJson, "table_name", out var tableNames))
            return [];

        var describeFn = _tools.OfType<AIFunction>()
            .FirstOrDefault(t => string.Equals(t.Name, "DescribeTable", StringComparison.OrdinalIgnoreCase));
        if (describeFn is null) return [];

        var overviews = new List<TableOverview>();
        foreach (var tableName in tableNames)
        {
            var describeResult = await describeFn.InvokeAsync(
                new AIFunctionArguments(new Dictionary<string, object?> { ["tableName"] = tableName }), ct);
            var describeJson = SchemaJsonParser.ExtractStringResult(describeResult);
            if (describeJson is not null && SchemaJsonParser.TryParseRowValues(describeJson, "column_name", out var columns))
                overviews.Add(new TableOverview(tableName, columns));
        }

        return overviews;
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

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using HrDashboard.Agents;
using HrDashboard.Agents.Models;
using Microsoft.JSInterop;

namespace HrDashboard.Web.Services;

public class ChatSessionService(
    IHrAgentService agent,
    IConversationRepository repo,
    IUsageRepository usage,
    IJSRuntime js,
    ILogger<ChatSessionService> logger,
    bool showRawStreamingOutput = false)
{
    public ConversationSummary? CurrentConversation { get; private set; }
    public List<MessageViewModel> Messages { get; } = [];
    public bool IsStreaming { get; private set; }
    public IReadOnlyList<HrMetricRow> CurrentMetrics { get; private set; } = [];
    public HrDataSet? ConfirmedChartDataSet { get; private set; }
    public HrChartRecommendation? ConfirmedChart { get; private set; }
    public List<ConversationSummary> Conversations { get; private set; } = [];
    public IReadOnlyList<TableOverview>? SchemaOverview { get; private set; }

    // When false (default), ChatThread keeps showing the "Thinking..." indicator for the
    // whole streaming phase instead of the raw accumulating text — a JSON-contract answer's
    // first streamed chunks ARE the raw JSON array, which otherwise flashes on screen before
    // SendAsync's post-processing below replaces it with the cleaned summary. The raw text is
    // always available via the Debug-level log line in SendAsync regardless of this flag;
    // set Chat:ShowRawStreamingOutput to true in appsettings.json to see it live in the UI too.
    public bool ShowRawStreamingOutput { get; } = showRawStreamingOutput;

    public event Action? OnChange;

    public async Task LoadUserConversationsAsync(string userId, CancellationToken ct = default)
    {
        Conversations = await repo.GetByUserAsync(userId, ct);
        Notify();
    }

    public async Task StartNewConversationAsync(string userId, CancellationToken ct = default)
    {
        CurrentConversation = await repo.CreateAsync(userId, "New conversation", ct);
        Messages.Clear();
        CurrentMetrics = [];
        Conversations = await repo.GetByUserAsync(userId, ct);
        Notify();
    }

    public async Task LoadConversationAsync(Guid conversationId, string userId, CancellationToken ct = default)
    {
        var summary = Conversations.FirstOrDefault(c => c.Id == conversationId);
        if (summary is null) return;

        CurrentConversation = summary;
        Messages.Clear();
        CurrentMetrics = [];

        var rows = await repo.GetMessagesForDisplayAsync(conversationId, ct);
        foreach (var row in rows)
        {
            HrDataSet? storedDataSet = null;
            var hasDataSet = row.MetricsJson is not null
                && HrDataSetParser.TryParse(row.MetricsJson, out storedDataSet);
            var legacyMetrics = row.MetricsJson is not null && !hasDataSet
                ? JsonSerializer.Deserialize<List<HrMetricRow>>(row.MetricsJson) ?? []
                : [];
            var vm = new MessageViewModel
            {
                Role = row.Role,
                Content = row.Content,
                DataSet = storedDataSet ?? (legacyMetrics.Count > 0
                    ? HrDataSet.FromLegacyMetrics(legacyMetrics)
                    : null),
                Metrics = legacyMetrics.Count > 0 ? legacyMetrics : null
            };
            Messages.Add(vm);
            if (row.Role == MessageRole.Assistant && vm.Metrics?.Count > 0)
                CurrentMetrics = vm.Metrics;
        }

        Notify();
    }

    public async Task RenameConversationAsync(
        Guid conversationId, string newTitle, string userId, CancellationToken ct = default)
    {
        var trimmed = newTitle.Trim();
        if (trimmed.Length == 0) return;

        await repo.UpdateTitleAsync(conversationId, trimmed, ct);

        if (CurrentConversation?.Id == conversationId)
            CurrentConversation = CurrentConversation with { Title = trimmed };

        Conversations = await repo.GetByUserAsync(userId, ct);
        Notify();
    }

    public async Task DeleteConversationAsync(
        Guid conversationId, string userId, CancellationToken ct = default)
    {
        await repo.DeleteAsync(conversationId, ct);

        if (CurrentConversation?.Id == conversationId)
        {
            CurrentConversation = null;
            Messages.Clear();
            CurrentMetrics = [];
        }

        Conversations = await repo.GetByUserAsync(userId, ct);
        Notify();
    }

    public async Task EnsureSchemaOverviewLoadedAsync(CancellationToken ct = default)
    {
        if (SchemaOverview is not null) return;

        try
        {
            SchemaOverview = await agent.GetSchemaOverviewAsync(ct);
            Notify();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // This runs from PromptBar's OnInitializedAsync, i.e. during page render — an
            // unhandled exception there crashes the whole page with a 500, not just this one
            // summary card (confirmed live: killing the MCP server mid-startup did exactly
            // that). The card is a nice-to-have, never load-bearing for the rest of the page,
            // so a schema-fetch failure (e.g. the MCP server isn't up yet) should just mean no
            // card this time — SchemaOverview stays null so a later retry (new conversation,
            // page reload) can still succeed once connectivity recovers.
            logger.LogWarning(ex, "Failed to load schema overview for the data-availability card");
        }
    }

    public async Task SendAsync(string prompt, string userId, CancellationToken ct = default)
    {
        if (IsStreaming || string.IsNullOrWhiteSpace(prompt)) return;

        // Create conversation on first message
        if (CurrentConversation is null)
            await StartNewConversationAsync(userId, ct);

        var conversationId = CurrentConversation!.Id;

        // Add and persist user message
        var userVm = MessageViewModel.FromUser(prompt);
        Messages.Add(userVm);
        await repo.AddMessageAsync(conversationId, MessageRole.User, prompt, ct: ct);

        // Add streaming assistant placeholder
        var assistantVm = MessageViewModel.StreamingAssistant();
        Messages.Add(assistantVm);
        IsStreaming = true;
        ConfirmedChartDataSet = null;
        ConfirmedChart = null;
        // Deliberately NOT clearing CurrentMetrics here: this runs before we know whether the
        // new request will even succeed. Clearing eagerly meant any error (a failed tool call,
        // a template error from the LLM provider, etc.) permanently blanked the results panel's
        // chart even though the previous successful exchange's chart was still meaningful to
        // show. CurrentMetrics is only ever overwritten below, once a new exchange actually
        // completes — so a failed exchange leaves the last-good chart exactly as it was.
        Notify();

        var totalStopwatch = Stopwatch.StartNew();

        try
        {
            // Fetch agent context (text only — no MetricsJson)
            var history = await repo.GetMessagesForAgentAsync(conversationId, ct);

            // Stream response chunks
            var streamStopwatch = Stopwatch.StartNew();
            await foreach (var chunk in agent.AskStreamAsync(history, prompt, ct))
            {
                assistantVm.Content += chunk;
                Notify();
            }
            logger.LogInformation("SendAsync: agent.AskStreamAsync took {ElapsedMs}ms", streamStopwatch.ElapsedMilliseconds);

            var pendingColumns = agent.LastPendingColumnOptions;
            if (pendingColumns is not null)
            {
                assistantVm.PendingColumns = pendingColumns;
                assistantVm.SelectedColumns = pendingColumns.GetDefaultSelectedColumns();
            }

            assistantVm.DataSet = agent.LastDataSet;
            if (assistantVm.DataSet is not null)
            {
                assistantVm.Metrics = null;
                CurrentMetrics = [];
            }

            // Raw model output before any cleanup — never shown to the end user (it can be a
            // bare JSON array), but logged at Debug level for troubleshooting. Enable via
            // Serilog:MinimumLevel:Override for this category, or turn on
            // Chat:ShowRawStreamingOutput to see it live in the chat UI instead.
            logger.LogDebug("Raw assistant response before cleanup for \"{Prompt}\": {Raw}", prompt, assistantVm.Content);

            // Strip any tool-call/tool-result scaffolding the local LLM may have echoed
            // before the intended payload, then parse metrics from the cleaned response.
            var cleaned = HrMetricParser.StripScaffolding(assistantVm.Content);
            if (assistantVm.DataSet is not null)
                cleaned = HrDataSetParser.ExtractDisplayText(assistantVm.Content);
            var arrayFound = HrMetricParser.TryParse(cleaned, out var metrics);
            assistantVm.Metrics = metrics;
            // Only replace the results panel's data when this turn actually produced a new
            // metrics array. A turn that didn't (a fallback message, an iteration-limit notice,
            // a plain conversational reply) has nothing new to show — leaving CurrentMetrics
            // alone keeps the last genuinely-relevant chart visible instead of blanking the
            // panel for an unrelated or failed exchange.
            if (arrayFound)
                CurrentMetrics = metrics;

            if (!arrayFound)
            {
                // See HrMetricParser.LooksLikeGenuineTextAnswer for why this distinction
                // matters: a purely conversational answer ("who are you") that never
                // attempted JSON is a legitimate response, not the bug-060 narration/
                // scaffolding failure the fallback below exists to catch.
                if (HrMetricParser.LooksLikeGenuineTextAnswer(cleaned))
                {
                    assistantVm.Content = cleaned;
                }
                else
                {
                    logger.LogWarning(
                        "No HrMetricRow array found in assistant response for \"{Prompt}\" — raw text: {RawText}",
                        prompt, cleaned);
                    assistantVm.Content = "I couldn't put together a clear summary for that — try rephrasing the question.";
                }
            }
            else
            {
                // The JSON array already backs the chart via CurrentMetrics — showing it again
                // in the chat transcript is redundant and confusing, so the bubble keeps only
                // the natural-language summary. The raw payload is still available for
                // debugging in the browser console instead.
                var displayText = HrMetricParser.ExtractDisplayText(cleaned);

                // The model sometimes emits only the JSON payload with no trailing summary
                // sentence at all (confirmed live 2026-08-23, a bare-object "who are you"
                // response) — an empty chat bubble reads as broken even though the data
                // parsed fine, so fall back to pointing at the results panel instead.
                assistantVm.Content = string.IsNullOrWhiteSpace(displayText)
                    ? "Here's what I found — see the results panel for details."
                    : displayText;
            }

            var consoleLogStopwatch = Stopwatch.StartNew();
            await LogMetricsToConsoleAsync(metrics);
            logger.LogInformation("SendAsync: console-log JS interop took {ElapsedMs}ms", consoleLogStopwatch.ElapsedMilliseconds);

            // Persist assistant message with MetricsJson
            var persistStopwatch = Stopwatch.StartNew();
            var metricsJson = assistantVm.DataSet is not null
                ? JsonSerializer.Serialize(new { dataset = assistantVm.DataSet })
                : metrics.Count > 0
                    ? JsonSerializer.Serialize(metrics)
                : null;
            var savedMessage = await repo.AddMessageAsync(
                conversationId, MessageRole.Assistant, assistantVm.Content, metricsJson, ct);
            logger.LogInformation("SendAsync: persist assistant message took {ElapsedMs}ms", persistStopwatch.ElapsedMilliseconds);

            // Best-effort usage logging — a transient DB error here must never overwrite
            // an assistant message that already streamed successfully.
            var turnUsage = agent.LastTurnUsage;
            if (turnUsage is not null)
            {
                try
                {
                    await usage.AddUsageRecordAsync(
                        savedMessage.Id, userId, turnUsage.Provider, turnUsage.Model,
                        turnUsage.InputTokens, turnUsage.OutputTokens, turnUsage.TotalTokens, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Failed to persist usage record for message {MessageId}", savedMessage.Id);
                }
            }

            // Auto-title on first exchange
            if (Messages.Count == 2 && CurrentConversation.Title == "New conversation")
            {
                var title = prompt.Length <= 60 ? prompt : prompt[..60];
                await repo.UpdateTitleAsync(conversationId, title, ct);
                CurrentConversation = CurrentConversation with { Title = title };
                Conversations = await repo.GetByUserAsync(userId, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "ChatSessionService.SendAsync failed");
            assistantVm.Content = $"[Error: {ex.Message}]";
        }
        finally
        {
            assistantVm.IsStreaming = false;
            IsStreaming = false;
            logger.LogInformation("SendAsync: total {ElapsedMs}ms for prompt \"{Prompt}\"", totalStopwatch.ElapsedMilliseconds, prompt);
            Notify();
        }
    }

    public bool ConfirmChart(MessageViewModel message)
    {
        var dataSet = message.DataSet;
        var recommendation = dataSet?.ChartRecommendation;
        if (dataSet is null || recommendation is null)
            return false;

        if (!dataSet.HasColumn(recommendation.XAxisColumn)
            || !dataSet.HasColumn(recommendation.YAxisColumn))
            return false;

        var yColumn = dataSet.Columns.First(column =>
            string.Equals(column.Name, recommendation.YAxisColumn, StringComparison.OrdinalIgnoreCase));
        if (!string.Equals(yColumn.Type, "number", StringComparison.OrdinalIgnoreCase))
            return false;

        foreach (var row in dataSet.Rows)
        {
            if (!row.TryGetValue(recommendation.YAxisColumn, out var value)
                || !TryGetNumber(value, out _))
                return false;
        }

        ConfirmedChartDataSet = dataSet;
        ConfirmedChart = recommendation;
        message.ChartConfirmed = true;
        Notify();
        return true;
    }

    private static bool TryGetNumber(JsonElement value, out double number)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number))
            return true;
        number = 0.0;

        return value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    private void Notify() => OnChange?.Invoke();

    // Surfaces the raw metrics payload in the browser dev tools console instead of the
    // chat transcript. Best-effort — a JS interop failure (e.g. during prerendering)
    // must never break the chat response itself.
    private async Task LogMetricsToConsoleAsync(IReadOnlyList<HrMetricRow> metrics)
    {
        if (metrics.Count == 0) return;

        try
        {
            await js.InvokeVoidAsync("console.log", "[HrDashboard] metrics:", metrics);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to log metrics to browser console");
        }
    }
}

using System.Diagnostics;
using System.Text.Json;
using HrDashboard.Agents;
using HrDashboard.Agents.Models;
using Microsoft.JSInterop;

namespace HrDashboard.Web.Services;

public class ChatSessionService(
    IHrAgentService agent,
    IConversationRepository repo,
    IJSRuntime js,
    ILogger<ChatSessionService> logger)
{
    public ConversationSummary? CurrentConversation { get; private set; }
    public List<MessageViewModel> Messages { get; } = [];
    public bool IsStreaming { get; private set; }
    public IReadOnlyList<HrMetricRow> CurrentMetrics { get; private set; } = [];
    public List<ConversationSummary> Conversations { get; private set; } = [];

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
            var vm = new MessageViewModel
            {
                Role = row.Role,
                Content = row.Content,
                Metrics = row.MetricsJson is not null
                    ? JsonSerializer.Deserialize<List<HrMetricRow>>(row.MetricsJson) ?? []
                    : null
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
        CurrentMetrics = [];
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

            // Strip any tool-call/tool-result scaffolding the local LLM may have echoed
            // before the intended payload, then parse metrics from the cleaned response.
            var cleaned = HrMetricParser.StripScaffolding(assistantVm.Content);
            var arrayFound = HrMetricParser.TryParse(cleaned, out var metrics);
            assistantVm.Metrics = metrics;
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
            var metricsJson = metrics.Count > 0
                ? JsonSerializer.Serialize(metrics)
                : null;
            await repo.AddMessageAsync(
                conversationId, MessageRole.Assistant, assistantVm.Content, metricsJson, ct);
            logger.LogInformation("SendAsync: persist assistant message took {ElapsedMs}ms", persistStopwatch.ElapsedMilliseconds);

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

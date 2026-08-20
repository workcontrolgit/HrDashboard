# Task 8 Brief: ChatSessionService + MessageViewModel

## Context
Task 8 of 13. Creates the scoped streaming state service and UI message model.
Repository: c:/apps/HrDashboard, branch: feature/claude-layout-redesign.
Tasks 1-7 complete.

Key interfaces from prior tasks:
- `IHrAgentService.AskStreamAsync(IEnumerable<(MessageRole Role, string Content)> history, string prompt, CancellationToken ct)` → `IAsyncEnumerable<string>`
- `IConversationRepository`: GetByUserAsync, CreateAsync, UpdateTitleAsync, GetMessagesForAgentAsync, GetMessagesForDisplayAsync, AddMessageAsync
- `ConversationSummary(Guid Id, string Title, DateTime CreatedAt)` — record in IConversationRepository.cs
- `MessageDisplay(Guid Id, MessageRole Role, string Content, string? MetricsJson, DateTime CreatedAt)` — record in IConversationRepository.cs
- `HrMetricParser.Parse(string llmText)` → `IReadOnlyList<HrMetricRow>` — static method in `HrDashboard.Agents.Models.HrMetricRow.cs`
- `MessageRole` enum: User | Assistant — in `HrDashboard.Agents.Models`

There is currently a `src/HrDashboard.Web/Services/_Placeholder.cs` file. DELETE it as part of this task — the real service files replace it.

## Global Constraints
- net10.0, nullable enabled, implicit usings enabled
- `ChatSessionService` must be `public` (not internal)
- No subagents — implement, build, commit, write report yourself

## Files to Create

### 1. `src/HrDashboard.Web/Services/MessageViewModel.cs`

```csharp
using HrDashboard.Agents.Models;

namespace HrDashboard.Web.Services;

public class MessageViewModel
{
    public MessageRole Role { get; init; }
    public string Content { get; set; } = string.Empty;
    public bool IsStreaming { get; set; }
    public IReadOnlyList<HrMetricRow>? Metrics { get; set; }

    public static MessageViewModel FromUser(string content) => new()
    {
        Role = MessageRole.User,
        Content = content
    };

    public static MessageViewModel StreamingAssistant() => new()
    {
        Role = MessageRole.Assistant,
        IsStreaming = true
    };
}
```

### 2. `src/HrDashboard.Web/Services/ChatSessionService.cs`

```csharp
using System.Text.Json;
using HrDashboard.Agents;
using HrDashboard.Agents.Models;

namespace HrDashboard.Web.Services;

public class ChatSessionService(
    IHrAgentService agent,
    IConversationRepository repo,
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
                    ? HrMetricParser.Parse(row.MetricsJson)
                    : null
            };
            Messages.Add(vm);
            if (row.Role == MessageRole.Assistant && vm.Metrics?.Count > 0)
                CurrentMetrics = vm.Metrics;
        }

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

        try
        {
            // Fetch agent context (text only — no MetricsJson)
            var history = await repo.GetMessagesForAgentAsync(conversationId, ct);

            // Stream response chunks
            await foreach (var chunk in agent.AskStreamAsync(history, prompt, ct))
            {
                assistantVm.Content += chunk;
                Notify();
            }

            // Parse metrics from completed response
            var metrics = HrMetricParser.Parse(assistantVm.Content);
            assistantVm.Metrics = metrics;
            CurrentMetrics = metrics;

            // Persist assistant message with MetricsJson
            var metricsJson = metrics.Count > 0
                ? JsonSerializer.Serialize(metrics)
                : null;
            await repo.AddMessageAsync(
                conversationId, MessageRole.Assistant, assistantVm.Content, metricsJson, ct);

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
            Notify();
        }
    }

    private void Notify() => OnChange?.Invoke();
}
```

## Steps
1. Delete `src/HrDashboard.Web/Services/_Placeholder.cs`
2. Create `src/HrDashboard.Web/Services/MessageViewModel.cs`
3. Create `src/HrDashboard.Web/Services/ChatSessionService.cs`
4. Run: `dotnet build src/HrDashboard.Web/HrDashboard.Web.csproj`
5. Verify: Build succeeded, 0 errors
6. Commit:
   ```
   git add src/HrDashboard.Web/Services/
   git commit -m "feat(web): add ChatSessionService and MessageViewModel for streaming conversation state"
   ```
7. Write report to: `.superpowers/sdd/2026-08-19-claude-layout-redesign/task-8-report.md`

## Report Contract
Status: DONE | DONE_WITH_CONCERNS | NEEDS_CONTEXT | BLOCKED
Commits: <sha>
Test summary: build result
Concerns: (if any)

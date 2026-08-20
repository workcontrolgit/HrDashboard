# Task 1 Brief: MessageRole enum + IConversationRepository interface

## Context
You are implementing Task 1 of 13 in the HrDashboard Claude-layout redesign.
This task creates the shared contracts that ALL subsequent tasks depend on.
Repository: c:/apps/HrDashboard, branch: feature/claude-layout-redesign.

## Global Constraints
- Target framework: net10.0
- Nullable: enabled; implicit usings: enabled
- HrMetricParser already exists in `src/HrDashboard.Agents/Models/HrMetricRow.cs` — do NOT touch it
- No subagents — implement, test (build), commit, and write your report yourself

## Your Job
Create exactly two files in the `HrDashboard.Agents` project.

### File 1: `src/HrDashboard.Agents/Models/MessageRole.cs`
```csharp
namespace HrDashboard.Agents.Models;

public enum MessageRole
{
    User,
    Assistant
}
```

### File 2: `src/HrDashboard.Agents/IConversationRepository.cs`
```csharp
using HrDashboard.Agents.Models;

namespace HrDashboard.Agents;

public interface IConversationRepository
{
    Task<List<ConversationSummary>> GetByUserAsync(string userId, CancellationToken ct = default);
    Task<ConversationSummary> CreateAsync(string userId, string title, CancellationToken ct = default);
    Task UpdateTitleAsync(Guid conversationId, string title, CancellationToken ct = default);

    /// <summary>Returns (Role, Content) only — no MetricsJson. Used for agent context.</summary>
    Task<List<(MessageRole Role, string Content)>> GetMessagesForAgentAsync(
        Guid conversationId, CancellationToken ct = default);

    /// <summary>Returns full message rows including MetricsJson. Used for display.</summary>
    Task<List<MessageDisplay>> GetMessagesForDisplayAsync(
        Guid conversationId, CancellationToken ct = default);

    Task<MessageDisplay> AddMessageAsync(
        Guid conversationId,
        MessageRole role,
        string content,
        string? metricsJson = null,
        CancellationToken ct = default);
}

public record ConversationSummary(Guid Id, string Title, DateTime CreatedAt);

public record MessageDisplay(
    Guid Id,
    MessageRole Role,
    string Content,
    string? MetricsJson,
    DateTime CreatedAt);
```

## Steps
1. Create both files with exactly the code above
2. Run: `dotnet build src/HrDashboard.Agents/HrDashboard.Agents.csproj`
3. Verify: Build succeeded, 0 errors
4. Commit:
   ```
   git add src/HrDashboard.Agents/Models/MessageRole.cs src/HrDashboard.Agents/IConversationRepository.cs
   git commit -m "feat(agents): add MessageRole enum and IConversationRepository interface"
   ```
5. Write your report to: `.superpowers/sdd/2026-08-19-claude-layout-redesign/task-1-report.md`

## Report Contract
Status: DONE | DONE_WITH_CONCERNS | NEEDS_CONTEXT | BLOCKED
Commits: <sha>
Test summary: build result (pass/fail, 0 errors)
Concerns: (if any)

# Task 4 Brief: ConversationRepository implementation

## Context
Task 4 of 13. Implements IConversationRepository in the Infrastructure project.
Repository: c:/apps/HrDashboard, branch: feature/claude-layout-redesign.

Tasks 1-3 complete:
- `IConversationRepository` (with `ConversationSummary` and `MessageDisplay` records) is in `src/HrDashboard.Agents/IConversationRepository.cs`
- `AppDbContext`, `Conversation`, `Message` entities are in `src/HrDashboard.Infrastructure/`
- `MessageRole` enum is in `src/HrDashboard.Agents/Models/MessageRole.cs`

## Global Constraints
- net10.0, nullable enabled, implicit usings enabled
- No subagents — implement, build, commit, write report yourself

## File to Create

### `src/HrDashboard.Infrastructure/Repositories/ConversationRepository.cs`

```csharp
using HrDashboard.Agents;
using HrDashboard.Agents.Models;
using HrDashboard.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;

namespace HrDashboard.Infrastructure.Repositories;

public class ConversationRepository(AppDbContext db) : IConversationRepository
{
    public async Task<List<ConversationSummary>> GetByUserAsync(
        string userId, CancellationToken ct = default)
    {
        return await db.Conversations
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new ConversationSummary(c.Id, c.Title, c.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task<ConversationSummary> CreateAsync(
        string userId, string title, CancellationToken ct = default)
    {
        var conv = new Conversation { UserId = userId, Title = title };
        db.Conversations.Add(conv);
        await db.SaveChangesAsync(ct);
        return new ConversationSummary(conv.Id, conv.Title, conv.CreatedAt);
    }

    public async Task UpdateTitleAsync(
        Guid conversationId, string title, CancellationToken ct = default)
    {
        await db.Conversations
            .Where(c => c.Id == conversationId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Title, title), ct);
    }

    public async Task<List<(MessageRole Role, string Content)>> GetMessagesForAgentAsync(
        Guid conversationId, CancellationToken ct = default)
    {
        return await db.Messages
            .Where(m => m.ConversationId == conversationId)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new ValueTuple<MessageRole, string>(m.Role, m.Content))
            .ToListAsync(ct);
    }

    public async Task<List<MessageDisplay>> GetMessagesForDisplayAsync(
        Guid conversationId, CancellationToken ct = default)
    {
        return await db.Messages
            .Where(m => m.ConversationId == conversationId)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new MessageDisplay(m.Id, m.Role, m.Content, m.MetricsJson, m.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task<MessageDisplay> AddMessageAsync(
        Guid conversationId,
        MessageRole role,
        string content,
        string? metricsJson = null,
        CancellationToken ct = default)
    {
        var msg = new Message
        {
            ConversationId = conversationId,
            Role = role,
            Content = content,
            MetricsJson = metricsJson
        };
        db.Messages.Add(msg);
        await db.SaveChangesAsync(ct);
        return new MessageDisplay(msg.Id, msg.Role, msg.Content, msg.MetricsJson, msg.CreatedAt);
    }
}
```

## Steps
1. Create the file above
2. Run: `dotnet build src/HrDashboard.Infrastructure/HrDashboard.Infrastructure.csproj`
3. Verify: Build succeeded, 0 errors
4. Commit:
   ```
   git add src/HrDashboard.Infrastructure/Repositories/ConversationRepository.cs
   git commit -m "feat(infra): implement ConversationRepository with agent/display read paths"
   ```
5. Write report to: `.superpowers/sdd/2026-08-19-claude-layout-redesign/task-4-report.md`

## Report Contract
Status: DONE | DONE_WITH_CONCERNS | NEEDS_CONTEXT | BLOCKED
Commits: <sha>
Test summary: build result
Concerns: (if any)

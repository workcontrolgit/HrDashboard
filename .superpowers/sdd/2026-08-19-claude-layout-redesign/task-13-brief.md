# Task 13 Brief: Dashboard.razor refactor + wire DI

## Context
Task 13 of 13 (final implementation task). Rewrites Dashboard.razor to host ChatThread and PromptBar, and registers ChatSessionService as scoped in Program.cs.
Repository: c:/apps/HrDashboard, branch: feature/claude-layout-redesign.
Tasks 1-12 complete.

Key interfaces:
- `ChatThread` — component in `HrDashboard.Web.Components.Chat` namespace, created in Task 11
- `PromptBar` — component in `HrDashboard.Web.Components.Chat` namespace, created in Task 11
- `ChatSessionService` — class in `HrDashboard.Web.Services` namespace, created in Task 8

## Global Constraints
- net10.0, MudBlazor v8
- No subagents — implement, build, commit, write report yourself

## Step 1: Register ChatSessionService in Program.cs

In `src/HrDashboard.Web/Program.cs`, find this comment line:
```
// Placeholder — Task 13 adds: builder.Services.AddScoped<ChatSessionService>();
```

Replace that comment line with:
```csharp
builder.Services.AddScoped<HrDashboard.Web.Services.ChatSessionService>();
```

## Step 2: Rewrite Dashboard.razor

Replace the ENTIRE content of `src/HrDashboard.Web/Components/Pages/Dashboard.razor` with:
```razor
@page "/"
@attribute [Microsoft.AspNetCore.Authorization.Authorize]

<PageTitle>HR Analytics</PageTitle>

<div style="display:flex;flex-direction:column;height:100%">
    <ChatThread />
    <PromptBar />
</div>
```

## Steps
1. Edit Program.cs: replace the placeholder comment with `builder.Services.AddScoped<HrDashboard.Web.Services.ChatSessionService>();`
2. Replace Dashboard.razor entirely with the exact content above
3. Run: `dotnet build src/HrDashboard.Web/HrDashboard.Web.csproj`
4. Verify: Build succeeded, 0 errors
5. Commit:
   ```
   git add src/HrDashboard.Web/Components/Pages/Dashboard.razor \
           src/HrDashboard.Web/Program.cs
   git commit -m "feat(web): refactor Dashboard to host ChatThread+PromptBar; register ChatSessionService"
   ```
6. Write report to: `.superpowers/sdd/2026-08-19-claude-layout-redesign/task-13-report.md`

## Report Contract
Status: DONE | DONE_WITH_CONCERNS | NEEDS_CONTEXT | BLOCKED
Commits: <sha>
Test summary: build result
Concerns: (if any)

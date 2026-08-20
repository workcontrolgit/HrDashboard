# Task 10 Brief: ConversationList component

## Context
Task 10 of 13. Creates the left panel component and uncomments it in MainLayout.
Repository: c:/apps/HrDashboard, branch: feature/claude-layout-redesign.
Tasks 1-9 complete.

Key interfaces:
- `ChatSessionService` (in `HrDashboard.Web.Services`) — injected into the component
  - `Conversations: List<ConversationSummary>`
  - `CurrentConversation: ConversationSummary?`
  - `LoadUserConversationsAsync(string userId, CancellationToken)` 
  - `LoadConversationAsync(Guid conversationId, string userId, CancellationToken)`
  - `OnChange: event Action?`
- `ConversationSummary(Guid Id, string Title, DateTime CreatedAt)` — record from `IConversationRepository`
- `AuthenticationStateProvider` — to get current userId (ClaimTypes.NameIdentifier)

`ChatSessionService` is NOT yet registered in DI (Task 13 does that). The component will compile fine — DI resolution happens at runtime.

## Global Constraints
- net10.0, MudBlazor v8
- No subagents — implement, build, commit, write report yourself

## Step 1: Create `src/HrDashboard.Web/Components/Layout/ConversationList.razor`

```razor
@inject ChatSessionService Session
@inject AuthenticationStateProvider AuthStateProvider
@implements IDisposable

<div style="display:flex;flex-direction:column;height:100%">

    <div class="conv-list">
        @if (Session.Conversations.Count == 0)
        {
            <MudText Typo="Typo.caption" Color="Color.Secondary" Class="pa-3">
                No conversations yet. Start one below.
            </MudText>
        }
        @foreach (var conv in Session.Conversations)
        {
            var isActive = Session.CurrentConversation?.Id == conv.Id;
            <div class="conv-item @(isActive ? "active" : "")"
                 @onclick="() => SelectConversationAsync(conv.Id)">
                <MudText Typo="Typo.body2" Style="overflow:hidden;text-overflow:ellipsis;white-space:nowrap">
                    @conv.Title
                </MudText>
                <MudText Typo="Typo.caption" Color="Color.Secondary">
                    @conv.CreatedAt.ToLocalTime().ToString("MMM d")
                </MudText>
            </div>
        }
    </div>

    <div class="conv-profile">
        <AuthorizeView>
            <Authorized>
                <div style="display:flex;align-items:center;gap:8px">
                    <MudIcon Icon="@Icons.Material.Filled.AccountCircle" Color="Color.Secondary" />
                    <MudText Typo="Typo.body2" Style="flex:1;overflow:hidden;text-overflow:ellipsis">
                        @context.User.Identity?.Name
                    </MudText>
                    <MudIconButton Icon="@Icons.Material.Filled.Logout"
                                   Size="Size.Small"
                                   Href="/account/logout"
                                   Title="Sign out" />
                </div>
            </Authorized>
        </AuthorizeView>
    </div>

</div>

@code {
    [Parameter] public EventCallback OnNewChat { get; set; }

    private string? _userId;

    protected override async Task OnInitializedAsync()
    {
        Session.OnChange += StateHasChanged;
        var auth = await AuthStateProvider.GetAuthenticationStateAsync();
        _userId = auth.User.FindFirst(
            System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        if (_userId is not null)
            await Session.LoadUserConversationsAsync(_userId);
    }

    private async Task SelectConversationAsync(Guid id)
    {
        if (_userId is null) return;
        await Session.LoadConversationAsync(id, _userId);
    }

    public void Dispose() => Session.OnChange -= StateHasChanged;
}
```

## Step 2: Uncomment ConversationList in MainLayout.razor

In `src/HrDashboard.Web/Components/Layout/MainLayout.razor`, find these lines:
```
@* TODO Task 10: uncomment when ConversationList is created *@
@* <ConversationList OnNewChat="NewChat" /> *@
```

Replace them with:
```razor
<ConversationList OnNewChat="NewChat" />
```

## Steps
1. Create ConversationList.razor as shown
2. Uncomment ConversationList in MainLayout.razor (remove the 2 TODO comment lines, replace with the active component tag)
3. Run: `dotnet build src/HrDashboard.Web/HrDashboard.Web.csproj`
4. Verify: Build succeeded, 0 errors
5. Commit:
   ```
   git add src/HrDashboard.Web/Components/Layout/ConversationList.razor \
           src/HrDashboard.Web/Components/Layout/MainLayout.razor
   git commit -m "feat(web): add ConversationList component with history and user profile"
   ```
6. Write report to: `.superpowers/sdd/2026-08-19-claude-layout-redesign/task-10-report.md`

## Report Contract
Status: DONE | DONE_WITH_CONCERNS | NEEDS_CONTEXT | BLOCKED
Commits: <sha>
Test summary: build result
Concerns: (if any)

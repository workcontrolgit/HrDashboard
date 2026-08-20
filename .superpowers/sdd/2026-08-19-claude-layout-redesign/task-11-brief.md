# Task 11 Brief: ChatThread and PromptBar components

## Context
Task 11 of 13. Creates the center panel components.
Repository: c:/apps/HrDashboard, branch: feature/claude-layout-redesign.
Tasks 1-10 complete.

Key interfaces:
- `ChatSessionService.Messages: List<MessageViewModel>` — subscribe to `OnChange`
- `ChatSessionService.IsStreaming: bool`
- `ChatSessionService.SendAsync(string prompt, string userId, CancellationToken)`
- `MessageViewModel.Role: MessageRole` — User or Assistant
- `MessageViewModel.Content: string` — grows chunk by chunk during streaming
- `MessageViewModel.IsStreaming: bool`
- `MessageRole` enum (User | Assistant) in `HrDashboard.Agents.Models`
- `AuthenticationStateProvider` — to get userId (ClaimTypes.NameIdentifier)

Create new directory `src/HrDashboard.Web/Components/Chat/`.

## Global Constraints
- net10.0, MudBlazor v8
- No subagents — implement, build, commit, write report yourself

## File 1: `src/HrDashboard.Web/Components/Chat/ChatThread.razor`

```razor
@inject ChatSessionService Session
@implements IDisposable

<div class="hr-messages" @ref="_scrollRef">
    @if (Session.Messages.Count == 0)
    {
        <div style="flex:1;display:flex;flex-direction:column;align-items:center;justify-content:center;opacity:0.5">
            <MudIcon Icon="@Icons.Material.Filled.Chat" Style="font-size:48px" />
            <MudText Typo="Typo.body1" Class="mt-2">Ask anything about your HR data</MudText>
        </div>
    }
    @foreach (var msg in Session.Messages)
    {
        @if (msg.Role == HrDashboard.Agents.Models.MessageRole.User)
        {
            <div class="msg-user">@msg.Content</div>
        }
        else
        {
            <div class="msg-assistant @(msg.IsStreaming && string.IsNullOrEmpty(msg.Content) ? "msg-streaming" : "")">
                @if (string.IsNullOrEmpty(msg.Content) && msg.IsStreaming)
                {
                    <MudProgressCircular Size="Size.Small" Indeterminate="true" />
                }
                else
                {
                    @msg.Content
                    @if (msg.IsStreaming)
                    {
                        <span class="msg-streaming"></span>
                    }
                }
            </div>
        }
    }
</div>

@code {
    private ElementReference _scrollRef;

    protected override void OnInitialized()
    {
        Session.OnChange += OnSessionChange;
    }

    private void OnSessionChange()
    {
        InvokeAsync(async () =>
        {
            StateHasChanged();
            await Task.Yield();
        });
    }

    public void Dispose() => Session.OnChange -= OnSessionChange;
}
```

## File 2: `src/HrDashboard.Web/Components/Chat/PromptBar.razor`

```razor
@inject ChatSessionService Session
@inject AuthenticationStateProvider AuthStateProvider
@implements IDisposable

<div class="hr-prompt-bar">
    <MudStack Row="true" AlignItems="AlignItems.End" Spacing="2">
        <MudTextField @bind-Value="_prompt"
                      Placeholder="Ask an HR analytics question..."
                      Variant="Variant.Outlined"
                      FullWidth="true"
                      Lines="2"
                      Disabled="Session.IsStreaming"
                      OnKeyDown="HandleKeyDown" />
        <MudIconButton Icon="@Icons.Material.Filled.Send"
                       Color="Color.Primary"
                       Size="Size.Large"
                       OnClick="SendAsync"
                       Disabled="Session.IsStreaming || string.IsNullOrWhiteSpace(_prompt)"
                       Title="Send (Ctrl+Enter)" />
    </MudStack>

    <MudStack Row="true" Wrap="Wrap.Wrap" Class="mt-2" Spacing="1">
        @foreach (var preset in _presets)
        {
            <MudChip T="string" OnClick="() => SelectPreset(preset)"
                     Color="Color.Default" Size="Size.Small"
                     Disabled="Session.IsStreaming">@preset</MudChip>
        }
    </MudStack>
</div>

@code {
    private string _prompt = string.Empty;
    private string? _userId;

    private readonly string[] _presets =
    [
        "Average salary by department",
        "Top 10 highest-paid employees",
        "Headcount per department",
        "Job salary ranges",
        "Departments with more than 5 employees"
    ];

    protected override async Task OnInitializedAsync()
    {
        Session.OnChange += StateHasChanged;
        var auth = await AuthStateProvider.GetAuthenticationStateAsync();
        _userId = auth.User.FindFirst(
            System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    }

    private void SelectPreset(string preset) => _prompt = preset;

    private async Task HandleKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && e.CtrlKey)
            await SendAsync();
    }

    private async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(_prompt) || Session.IsStreaming || _userId is null) return;
        var prompt = _prompt;
        _prompt = string.Empty;
        await Session.SendAsync(prompt, _userId);
    }

    public void Dispose() => Session.OnChange -= StateHasChanged;
}
```

## Steps
1. Create `src/HrDashboard.Web/Components/Chat/` directory
2. Create ChatThread.razor with exact content above
3. Create PromptBar.razor with exact content above
4. Run: `dotnet build src/HrDashboard.Web/HrDashboard.Web.csproj`
5. Verify: Build succeeded, 0 errors
6. Commit:
   ```
   git add src/HrDashboard.Web/Components/Chat/
   git commit -m "feat(web): add ChatThread and PromptBar components"
   ```
7. Write report to: `.superpowers/sdd/2026-08-19-claude-layout-redesign/task-11-report.md`

## Report Contract
Status: DONE | DONE_WITH_CONCERNS | NEEDS_CONTEXT | BLOCKED
Commits: <sha>
Test summary: build result
Concerns: (if any)

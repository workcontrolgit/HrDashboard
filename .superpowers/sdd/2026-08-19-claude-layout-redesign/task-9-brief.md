# Task 9 Brief: MainLayout 3-panel shell + CSS

## Context
Task 9 of 13. Rewrites MainLayout and app.css for the 3-panel shell.
Repository: c:/apps/HrDashboard, branch: feature/claude-layout-redesign.
Tasks 1-8 complete.

Note: `ConversationList` and `ResultsPanel` components don't exist yet (Tasks 10 and 12). The build will warn/error on them. Handle by temporarily commenting out the component references in MainLayout — add TODO comments so Task 10/12 can uncomment them.

## Global Constraints
- net10.0, MudBlazor v8
- No subagents — implement, build, commit, write report yourself

## File 1: Replace `src/HrDashboard.Web/wwwroot/app.css` ENTIRELY with:

```css
html, body {
    height: 100%;
    margin: 0;
    overflow: hidden;
}

/* Three-panel shell */
.hr-shell {
    display: flex;
    height: 100vh;
    overflow: hidden;
}

/* Left panel */
.hr-left {
    width: 260px;
    min-width: 260px;
    display: flex;
    flex-direction: column;
    border-right: 1px solid rgba(0,0,0,0.12);
    background: var(--mud-palette-surface);
    transition: width 0.2s ease, min-width 0.2s ease;
    overflow: hidden;
}

.hr-left.collapsed {
    width: 0;
    min-width: 0;
}

/* Center panel */
.hr-center {
    flex: 1;
    display: flex;
    flex-direction: column;
    overflow: hidden;
    min-width: 0;
}

.hr-messages {
    flex: 1;
    overflow-y: auto;
    padding: 16px;
    display: flex;
    flex-direction: column;
    gap: 12px;
}

.hr-prompt-bar {
    border-top: 1px solid rgba(0,0,0,0.12);
    padding: 12px 16px;
    background: var(--mud-palette-surface);
}

/* Right panel */
.hr-right {
    width: 360px;
    min-width: 360px;
    display: flex;
    flex-direction: column;
    border-left: 1px solid rgba(0,0,0,0.12);
    background: var(--mud-palette-surface);
    transition: width 0.2s ease, min-width 0.2s ease;
    overflow: hidden;
}

.hr-right.collapsed {
    width: 0;
    min-width: 0;
}

.hr-right-content {
    flex: 1;
    overflow-y: auto;
    padding: 16px;
}

/* Message bubbles */
.msg-user {
    align-self: flex-end;
    background: var(--mud-palette-primary);
    color: var(--mud-palette-primary-text);
    border-radius: 18px 18px 4px 18px;
    padding: 10px 16px;
    max-width: 70%;
    word-break: break-word;
}

.msg-assistant {
    align-self: flex-start;
    background: var(--mud-palette-background-grey);
    border-radius: 18px 18px 18px 4px;
    padding: 10px 16px;
    max-width: 80%;
    word-break: break-word;
    white-space: pre-wrap;
}

.msg-streaming::after {
    content: '▋';
    animation: blink 1s step-end infinite;
    margin-left: 2px;
}

@keyframes blink {
    50% { opacity: 0; }
}

/* Auth pages */
.auth-container {
    display: flex;
    align-items: center;
    justify-content: center;
    min-height: 100vh;
    background: var(--mud-palette-background-grey);
}

.auth-card {
    width: 100%;
    max-width: 420px;
}

/* Conversation list */
.conv-list {
    flex: 1;
    overflow-y: auto;
}

.conv-item {
    padding: 8px 12px;
    cursor: pointer;
    border-radius: 8px;
    margin: 2px 8px;
    transition: background 0.15s;
}

.conv-item:hover {
    background: rgba(0,0,0,0.06);
}

.conv-item.active {
    background: var(--mud-palette-primary-lighten);
}

.conv-profile {
    padding: 12px;
    border-top: 1px solid rgba(0,0,0,0.12);
}
```

## File 2: Replace `src/HrDashboard.Web/Components/Layout/MainLayout.razor` ENTIRELY with:

```razor
@inherits LayoutComponentBase
@inject NavigationManager Nav

<MudThemeProvider />
<MudPopoverProvider />
<MudDialogProvider />
<MudSnackbarProvider />

<div class="hr-shell">

    <!-- Left panel -->
    <div class="hr-left @(_leftOpen ? "" : "collapsed")">
        <div style="display:flex;align-items:center;padding:8px 8px 0 8px;gap:4px">
            <MudIconButton Icon="@Icons.Material.Filled.Menu"
                           Size="Size.Small"
                           OnClick="() => _leftOpen = !_leftOpen"
                           Title="Toggle sidebar" />
            <MudIconButton Icon="@Icons.Material.Filled.Add"
                           Size="Size.Small"
                           Color="Color.Primary"
                           OnClick="NewChat"
                           Title="New conversation" />
        </div>
        @* TODO Task 10: uncomment when ConversationList is created *@
        @* <ConversationList OnNewChat="NewChat" /> *@
    </div>

    <!-- Center panel -->
    <div class="hr-center">
        <div style="display:flex;align-items:center;padding:8px 12px;border-bottom:1px solid rgba(0,0,0,0.12);gap:8px">
            @if (!_leftOpen)
            {
                <MudIconButton Icon="@Icons.Material.Filled.Menu"
                               Size="Size.Small"
                               OnClick="() => _leftOpen = true"
                               Title="Open sidebar" />
            }
            <MudText Typo="Typo.subtitle1" Style="flex:1">HR Analytics Portal</MudText>
            @if (!_rightOpen)
            {
                <MudIconButton Icon="@Icons.Material.Filled.BarChart"
                               Size="Size.Small"
                               OnClick="() => _rightOpen = true"
                               Title="Show results panel" />
            }
        </div>
        @Body
    </div>

    <!-- Right panel -->
    <div class="hr-right @(_rightOpen ? "" : "collapsed")">
        <div style="display:flex;align-items:center;padding:8px;border-bottom:1px solid rgba(0,0,0,0.12)">
            <MudText Typo="Typo.subtitle2" Style="flex:1">Results</MudText>
            <MudIconButton Icon="@Icons.Material.Filled.ChevronRight"
                           Size="Size.Small"
                           OnClick="() => _rightOpen = false"
                           Title="Hide results panel" />
        </div>
        <div class="hr-right-content">
            @* TODO Task 12: uncomment when ResultsPanel is created *@
            @* <ResultsPanel /> *@
        </div>
    </div>

</div>

@code {
    private bool _leftOpen = true;
    private bool _rightOpen = true;

    private void NewChat()
    {
        Nav.NavigateTo("/", forceLoad: false);
    }
}
```

## Steps
1. Replace app.css with exact content above
2. Replace MainLayout.razor with exact content above (with the two component references commented out)
3. Run: `dotnet build src/HrDashboard.Web/HrDashboard.Web.csproj`
4. Verify: Build succeeded, 0 errors
5. Commit:
   ```
   git add src/HrDashboard.Web/Components/Layout/MainLayout.razor \
           src/HrDashboard.Web/wwwroot/app.css
   git commit -m "feat(web): 3-panel MainLayout shell with collapsible left/right panels and CSS"
   ```
6. Write report to: `.superpowers/sdd/2026-08-19-claude-layout-redesign/task-9-report.md`

## Report Contract
Status: DONE | DONE_WITH_CONCERNS | NEEDS_CONTEXT | BLOCKED
Commits: <sha>
Test summary: build result
Concerns: (if any)

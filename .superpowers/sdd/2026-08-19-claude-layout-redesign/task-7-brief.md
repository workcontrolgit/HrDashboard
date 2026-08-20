# Task 7 Brief: Login and Register pages

## Context
Task 7 of 13. Creates the two auth pages.
Repository: c:/apps/HrDashboard, branch: feature/claude-layout-redesign.
Tasks 1-6 complete. Identity is wired, LoginPath="/login", cookie auth configured.

## Global Constraints
- net10.0, nullable enabled
- No subagents — implement, build, commit, write report yourself
- Pages go in `src/HrDashboard.Web/Components/Pages/Auth/`
- Both pages must NOT have @rendermode directive (inherited from App.razor as InteractiveServer)

## Files to Create

### 1. `src/HrDashboard.Web/Components/Pages/Auth/Login.razor`

```razor
@page "/login"
@using Microsoft.AspNetCore.Identity
@inject SignInManager<HrDashboard.Infrastructure.AppUser> SignInManager
@inject NavigationManager Nav

<PageTitle>Sign in — HR Analytics</PageTitle>

<div class="auth-container">
    <MudPaper Class="auth-card pa-8" Elevation="3">
        <MudText Typo="Typo.h5" Class="mb-1">HR Analytics Portal</MudText>
        <MudText Typo="Typo.body2" Color="Color.Secondary" Class="mb-6">Sign in to continue</MudText>

        @if (_error is not null)
        {
            <MudAlert Severity="Severity.Error" Class="mb-4">@_error</MudAlert>
        }

        <MudTextField @bind-Value="_email" Label="Email" Variant="Variant.Outlined"
                      InputType="InputType.Email" Class="mb-3" FullWidth="true" />
        <MudTextField @bind-Value="_password" Label="Password" Variant="Variant.Outlined"
                      InputType="InputType.Password" Class="mb-4" FullWidth="true" />

        <MudButton Variant="Variant.Filled" Color="Color.Primary" FullWidth="true"
                   OnClick="LoginAsync" Disabled="_loading">
            @if (_loading) { <MudProgressCircular Size="Size.Small" Indeterminate="true" /> }
            else { <span>Sign in</span> }
        </MudButton>

        <MudText Align="Align.Center" Class="mt-4" Typo="Typo.body2">
            No account? <MudLink Href="/register">Register</MudLink>
        </MudText>
    </MudPaper>
</div>

@code {
    private string _email = string.Empty;
    private string _password = string.Empty;
    private string? _error;
    private bool _loading;

    private async Task LoginAsync()
    {
        _loading = true;
        _error = null;
        var result = await SignInManager.PasswordSignInAsync(
            _email, _password, isPersistent: true, lockoutOnFailure: false);

        if (result.Succeeded)
            Nav.NavigateTo("/", forceLoad: true);
        else
            _error = "Invalid email or password.";

        _loading = false;
    }
}
```

### 2. `src/HrDashboard.Web/Components/Pages/Auth/Register.razor`

```razor
@page "/register"
@using Microsoft.AspNetCore.Identity
@inject UserManager<HrDashboard.Infrastructure.AppUser> UserManager
@inject SignInManager<HrDashboard.Infrastructure.AppUser> SignInManager
@inject NavigationManager Nav

<PageTitle>Register — HR Analytics</PageTitle>

<div class="auth-container">
    <MudPaper Class="auth-card pa-8" Elevation="3">
        <MudText Typo="Typo.h5" Class="mb-1">Create account</MudText>
        <MudText Typo="Typo.body2" Color="Color.Secondary" Class="mb-6">HR Analytics Portal</MudText>

        @if (_errors.Count > 0)
        {
            <MudAlert Severity="Severity.Error" Class="mb-4">
                @foreach (var e in _errors) { <div>@e</div> }
            </MudAlert>
        }

        <MudTextField @bind-Value="_email" Label="Email" Variant="Variant.Outlined"
                      InputType="InputType.Email" Class="mb-3" FullWidth="true" />
        <MudTextField @bind-Value="_password" Label="Password" Variant="Variant.Outlined"
                      InputType="InputType.Password" Class="mb-3" FullWidth="true" />
        <MudTextField @bind-Value="_confirm" Label="Confirm password" Variant="Variant.Outlined"
                      InputType="InputType.Password" Class="mb-4" FullWidth="true" />

        <MudButton Variant="Variant.Filled" Color="Color.Primary" FullWidth="true"
                   OnClick="RegisterAsync" Disabled="_loading">
            @if (_loading) { <MudProgressCircular Size="Size.Small" Indeterminate="true" /> }
            else { <span>Register</span> }
        </MudButton>

        <MudText Align="Align.Center" Class="mt-4" Typo="Typo.body2">
            Already have an account? <MudLink Href="/login">Sign in</MudLink>
        </MudText>
    </MudPaper>
</div>

@code {
    private string _email = string.Empty;
    private string _password = string.Empty;
    private string _confirm = string.Empty;
    private List<string> _errors = [];
    private bool _loading;

    private async Task RegisterAsync()
    {
        _errors = [];
        if (_password != _confirm)
        {
            _errors = ["Passwords do not match."];
            return;
        }

        _loading = true;
        var user = new HrDashboard.Infrastructure.AppUser { UserName = _email, Email = _email };
        var result = await UserManager.CreateAsync(user, _password);

        if (result.Succeeded)
        {
            await SignInManager.SignInAsync(user, isPersistent: true);
            Nav.NavigateTo("/", forceLoad: true);
        }
        else
        {
            _errors = result.Errors.Select(e => e.Description).ToList();
        }

        _loading = false;
    }
}
```

## Steps
1. Create both files above exactly as specified
2. Run: `dotnet build src/HrDashboard.Web/HrDashboard.Web.csproj`
3. Verify: Build succeeded, 0 errors
4. Commit:
   ```
   git add src/HrDashboard.Web/Components/Pages/Auth/
   git commit -m "feat(web): add Login and Register pages with ASP.NET Core Identity"
   ```
5. Write report to: `.superpowers/sdd/2026-08-19-claude-layout-redesign/task-7-report.md`

## Report Contract
Status: DONE | DONE_WITH_CONCERNS | NEEDS_CONTEXT | BLOCKED
Commits: <sha>
Test summary: build result
Concerns: (if any)

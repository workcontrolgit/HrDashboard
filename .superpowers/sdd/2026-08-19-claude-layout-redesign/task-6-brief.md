# Task 6 Brief: Identity + auth wiring

## Context
Task 6 of 13. Wires Identity, auth middleware, CascadingAuthenticationState, and route guard.
Repository: c:/apps/HrDashboard, branch: feature/claude-layout-redesign.
Tasks 1-5 complete. Infrastructure project, entities, repository, and migration all exist.

## Global Constraints
- net10.0, nullable enabled, implicit usings enabled
- No subagents — implement, build, commit, write report yourself

## Files to modify/create

### 1. Replace `src/HrDashboard.Web/Program.cs` ENTIRELY with:

```csharp
using Azure.Identity;
using HrDashboard.Agents;
using HrDashboard.Infrastructure;
using HrDashboard.Infrastructure.Repositories;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using MudBlazor.Services;
using OllamaSharp;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Configuration.AddUserSecrets<Program>(optional: true);
    builder.WebHost.UseStaticWebAssets();

    if (builder.Environment.IsProduction())
    {
        var vaultUri = builder.Configuration["AzureKeyVault:VaultUri"];
        if (!string.IsNullOrWhiteSpace(vaultUri))
        {
            builder.Configuration.AddAzureKeyVault(
                new Uri(vaultUri), new DefaultAzureCredential());
            Log.Information("Azure Key Vault loaded from {Uri}", vaultUri);
        }
        else
        {
            Log.Warning("AzureKeyVault:VaultUri not configured — skipping Key Vault");
        }
    }

    builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration));

    // ── SQL Server + Identity ────────────────────────────────────────────────
    var connStr = builder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Missing ConnectionStrings:DefaultConnection");

    builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlServer(connStr));

    builder.Services.AddIdentity<AppUser, IdentityRole>(o =>
    {
        o.Password.RequireDigit = false;
        o.Password.RequireUppercase = false;
        o.Password.RequireNonAlphanumeric = false;
        o.Password.RequiredLength = 6;
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();

    builder.Services.ConfigureApplicationCookie(o =>
    {
        o.LoginPath = "/login";
        o.AccessDeniedPath = "/login";
    });

    builder.Services.AddScoped<IConversationRepository, ConversationRepository>();

    // ── IChatClient ──────────────────────────────────────────────────────────
    var provider = builder.Configuration["AI:Provider"] ?? "Ollama";

    builder.Services.AddSingleton<IChatClient>(_ =>
    {
        if (string.Equals(provider, "AzureOpenAI", StringComparison.OrdinalIgnoreCase))
        {
            var endpoint   = builder.Configuration["AI:AzureOpenAI:Endpoint"]
                ?? throw new InvalidOperationException("Missing AI:AzureOpenAI:Endpoint");
            var deployment = builder.Configuration["AI:AzureOpenAI:DeploymentName"] ?? "gpt-4o";
            var apiKey     = builder.Configuration["AI:AzureOpenAI:ApiKey"];

            var azureClient = string.IsNullOrWhiteSpace(apiKey)
                ? new Azure.AI.OpenAI.AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential())
                : new Azure.AI.OpenAI.AzureOpenAIClient(new Uri(endpoint), new System.ClientModel.ApiKeyCredential(apiKey));

            return azureClient.GetChatClient(deployment).AsIChatClient();
        }

        var ollamaEndpoint = builder.Configuration["AI:Ollama:Endpoint"] ?? "http://localhost:11434";
        var model          = builder.Configuration["AI:Ollama:Model"]    ?? "llama3.1";
        return (IChatClient)new OllamaApiClient(new Uri(ollamaEndpoint), model);
    });

    builder.Services.AddScoped<IHrAgentService>(sp =>
    {
        var chatClient = sp.GetRequiredService<IChatClient>();
        var endpoint   = builder.Configuration["McpServer:Endpoint"] ?? "http://localhost:5200/mcp";
        var logger     = sp.GetRequiredService<ILogger<HrAgentService>>();
        return new HrAgentService(chatClient, endpoint, logger);
    });

    // ChatSessionService registered in Task 13
    // builder.Services.AddScoped<HrDashboard.Web.Services.ChatSessionService>();

    // ── Blazor + MudBlazor ───────────────────────────────────────────────────
    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents();
    builder.Services.AddMudServices();
    builder.Services.AddCascadingAuthenticationState();

    var app = builder.Build();

    // ── Auto-migrate on startup ───────────────────────────────────────────────
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
    }

    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error");
        app.UseHsts();
    }

    app.UseHttpsRedirection();
    app.UseStaticFiles();
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseAntiforgery();

    // ── Logout endpoint ──────────────────────────────────────────────────────
    app.MapGet("/account/logout", async (SignInManager<AppUser> signInManager) =>
    {
        await signInManager.SignOutAsync();
        return Results.LocalRedirect("/login");
    });

    app.MapRazorComponents<HrDashboard.Web.Components.App>()
        .AddInteractiveServerRenderMode()
        .WithStaticAssets();

    await app.RunAsync();
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    Log.Fatal(ex, "HrDashboard.Web terminated unexpectedly");
    Environment.ExitCode = 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
```

### 2. Replace `src/HrDashboard.Web/Components/App.razor` ENTIRELY with:

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>HR Analytics Portal</title>
    <base href="/" />
    <link href="https://fonts.googleapis.com/css?family=Roboto:300,400,500,700&display=swap" rel="stylesheet" />
    <link href="_content/MudBlazor/MudBlazor.min.css" rel="stylesheet" />
    <link rel="stylesheet" href="app.css" />
    <HeadOutlet />
</head>
<body>
    <CascadingAuthenticationState>
        <Routes @rendermode="InteractiveServer" />
    </CascadingAuthenticationState>
    <script src="_framework/blazor.web.js"></script>
    <script src="_content/MudBlazor/MudBlazor.min.js"></script>
</body>
</html>
```

### 3. Replace `src/HrDashboard.Web/Components/Routes.razor` ENTIRELY with:

```razor
<Router AppAssembly="typeof(App).Assembly">
    <Found Context="routeData">
        <AuthorizeRouteView RouteData="routeData" DefaultLayout="typeof(Layout.MainLayout)">
            <NotAuthorized>
                <RedirectToLogin />
            </NotAuthorized>
        </AuthorizeRouteView>
        <FocusOnNavigate RouteData="routeData" Selector="h1" />
    </Found>
</Router>
```

### 4. Create `src/HrDashboard.Web/Components/Layout/RedirectToLogin.razor`:

```razor
@inject NavigationManager Nav

@code {
    protected override void OnInitialized()
    {
        Nav.NavigateTo("/login", forceLoad: false);
    }
}
```

### 5. Replace `src/HrDashboard.Web/Components/_Imports.razor` ENTIRELY with:

```razor
@using Microsoft.AspNetCore.Components
@using Microsoft.AspNetCore.Components.Authorization
@using Microsoft.AspNetCore.Components.Routing
@using Microsoft.AspNetCore.Components.Web
@using static Microsoft.AspNetCore.Components.Web.RenderMode
@using Microsoft.AspNetCore.Components.Web.Virtualization
@using Microsoft.AspNetCore.Components.Forms
@using HrDashboard.Web.Components
@using HrDashboard.Web.Components.Layout
@using HrDashboard.Web.Components.Pages
@using HrDashboard.Web.Services
@using MudBlazor
@using HrDashboard.Agents
@using HrDashboard.Agents.Models
@using Microsoft.Extensions.Logging
```

## Steps
1. Create/replace all 5 files above with the exact content shown
2. Run: `dotnet build src/HrDashboard.Web/HrDashboard.Web.csproj`
3. Verify: Build succeeded, 0 errors. (RedirectToLogin and AuthorizeRouteView may produce warnings about missing Login page — acceptable since Task 7 creates it)
4. Commit:
   ```
   git add src/HrDashboard.Web/Program.cs \
           src/HrDashboard.Web/Components/App.razor \
           src/HrDashboard.Web/Components/Routes.razor \
           src/HrDashboard.Web/Components/_Imports.razor \
           src/HrDashboard.Web/Components/Layout/RedirectToLogin.razor
   git commit -m "feat(web): wire Identity, auth middleware, CascadingAuthenticationState, and route guard"
   ```
5. Write report to: `.superpowers/sdd/2026-08-19-claude-layout-redesign/task-6-report.md`

## Report Contract
Status: DONE | DONE_WITH_CONCERNS | NEEDS_CONTEXT | BLOCKED
Commits: <sha>
Test summary: build result
Concerns: (if any)

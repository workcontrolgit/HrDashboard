using Azure.Identity;
using Microsoft.AspNetCore.Mvc;
using HrDashboard.Agents;
using HrDashboard.Infrastructure;
using HrDashboard.Infrastructure.Repositories;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using MudBlazor.Services;
using OllamaSharp;
using Serilog;

var logBase = Path.Combine(SolutionRoot(), "logs", "web");

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(logBase, "info", "info-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7,
        restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information)
    .WriteTo.File(
        Path.Combine(logBase, "error", "error-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Error)
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

    builder.Host.UseSerilog((ctx, cfg) => cfg
        .ReadFrom.Configuration(ctx.Configuration)
        .WriteTo.Console()
        .WriteTo.File(
            Path.Combine(logBase, "info", "info-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 7,
            restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information)
        .WriteTo.File(
            Path.Combine(logBase, "error", "error-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 30,
            restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Error));

    // ── SQL Server + Identity ────────────────────────────────────────────────
    var connStr = builder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Missing ConnectionStrings:DefaultConnection");

    builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseSqlServer(connStr));

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

        if (string.Equals(provider, "Nvidia", StringComparison.OrdinalIgnoreCase))
        {
            // NVIDIA's NIM catalog (https://build.nvidia.com) exposes an OpenAI-compatible
            // /v1/chat/completions endpoint, so the plain OpenAI client works here — just
            // pointed at NVIDIA's base URL instead of api.openai.com.
            var endpoint = builder.Configuration["AI:Nvidia:Endpoint"] ?? "https://integrate.api.nvidia.com/v1";
            var model    = builder.Configuration["AI:Nvidia:Model"] ?? "meta/llama-3.1-8b-instruct";
            var apiKey   = builder.Configuration["AI:Nvidia:ApiKey"]
                ?? throw new InvalidOperationException("Missing AI:Nvidia:ApiKey");

            var nvidiaClient = new OpenAI.OpenAIClient(
                new System.ClientModel.ApiKeyCredential(apiKey),
                new OpenAI.OpenAIClientOptions { Endpoint = new Uri(endpoint) });

            return nvidiaClient.GetChatClient(model).AsIChatClient();
        }

        var ollamaEndpoint = builder.Configuration["AI:Ollama:Endpoint"] ?? "http://localhost:11434";
        var ollamaModel    = builder.Configuration["AI:Ollama:Model"]    ?? "llama3.1";
        return (IChatClient)new OllamaApiClient(new Uri(ollamaEndpoint), ollamaModel);
    });

    builder.Services.AddScoped<IHrAgentService>(sp =>
    {
        var chatClient = sp.GetRequiredService<IChatClient>();
        var endpoint   = builder.Configuration["McpServer:Endpoint"] ?? "http://localhost:5200/mcp";
        var logger     = sp.GetRequiredService<ILogger<HrAgentService>>();
        return new HrAgentService(chatClient, endpoint, logger);
    });

    builder.Services.AddScoped<HrDashboard.Web.Services.ChatSessionService>();

    // ── Blazor + MudBlazor ───────────────────────────────────────────────────
    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents();
    builder.Services.AddMudServices();
    builder.Services.AddCascadingAuthenticationState();

    var app = builder.Build();

    // ── Log active AI provider/model so it's visible without digging through config ──
    var resolvedModel = provider switch
    {
        var p when string.Equals(p, "AzureOpenAI", StringComparison.OrdinalIgnoreCase) =>
            builder.Configuration["AI:AzureOpenAI:DeploymentName"] ?? "gpt-4o",
        var p when string.Equals(p, "Nvidia", StringComparison.OrdinalIgnoreCase) =>
            builder.Configuration["AI:Nvidia:Model"] ?? "meta/llama-3.1-8b-instruct",
        _ => builder.Configuration["AI:Ollama:Model"] ?? "llama3.1"
    };
    Log.Information("AI provider: {Provider} | Model: {Model}", provider, resolvedModel);

    // ── Auto-migrate on startup ───────────────────────────────────────────────
    await using (var db = await app.Services
        .GetRequiredService<IDbContextFactory<AppDbContext>>()
        .CreateDbContextAsync())
    {
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

    // ── Auth endpoints (run in real HTTP context — SignalR circuit cannot write cookies) ──
    app.MapPost("/account/login-action", async (
        [FromForm] string email,
        [FromForm] string password,
        SignInManager<AppUser> signInManager) =>
    {
        var result = await signInManager.PasswordSignInAsync(
            email, password, isPersistent: true, lockoutOnFailure: false);
        return result.Succeeded
            ? Results.LocalRedirect("/")
            : Results.LocalRedirect($"/login?error=invalid&email={Uri.EscapeDataString(email)}");
    });

    app.MapPost("/account/register-action", async (
        [FromForm] string email,
        [FromForm] string password,
        [FromForm] string confirm,
        UserManager<AppUser> userManager,
        SignInManager<AppUser> signInManager) =>
    {
        if (password != confirm)
            return Results.LocalRedirect(
                $"/register?error={Uri.EscapeDataString("Passwords do not match.")}&email={Uri.EscapeDataString(email)}");

        var user = new AppUser { UserName = email, Email = email };
        var result = await userManager.CreateAsync(user, password);
        if (result.Succeeded)
        {
            await signInManager.SignInAsync(user, isPersistent: true);
            return Results.LocalRedirect("/");
        }

        var errors = string.Join(" ", result.Errors.Select(e => e.Description));
        return Results.LocalRedirect(
            $"/register?error={Uri.EscapeDataString(errors)}&email={Uri.EscapeDataString(email)}");
    });

    app.MapPost("/account/logout", async (SignInManager<AppUser> signInManager) =>
    {
        await signInManager.SignOutAsync();
        return Results.LocalRedirect("/login");
    }).RequireAuthorization();

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

static string SolutionRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !dir.GetFiles("*.slnx").Any() && !dir.GetFiles("*.sln").Any())
        dir = dir.Parent;
    return dir?.FullName ?? AppContext.BaseDirectory;
}

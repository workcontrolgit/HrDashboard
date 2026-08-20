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

    builder.Services.AddScoped<HrDashboard.Web.Services.ChatSessionService>();

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

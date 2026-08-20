# Claude-Layout Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Redesign HrDashboard into a three-panel Claude-like layout with persistent SQL Server conversation history, streaming AI responses, and ASP.NET Core Identity authentication.

**Architecture:** A new `HrDashboard.Infrastructure` project owns EF Core, Identity, and `ConversationRepository`. `HrDashboard.Agents` gains `IConversationRepository` interface and `AskStreamAsync`. `HrDashboard.Web` gains a `ChatSessionService`, a 3-panel `MainLayout`, and four new Razor components (ConversationList, ChatThread, PromptBar, ResultsPanel).

**Tech Stack:** .NET 10, Blazor Server, MudBlazor v8, ASP.NET Core Identity, EF Core 10 + SQL Server, Microsoft.Extensions.AI 10.9.0

**Spec:** `docs/superpowers/specs/2026-08-19-claude-layout-redesign-design.md`

## Global Constraints

- Target framework: `net10.0` for all projects
- Nullable: enabled; implicit usings: enabled
- EF Core / Identity packages: version `10.*`
- MudBlazor: version `8.*` (already installed)
- Microsoft.Extensions.AI: version `10.9.0` (already installed)
- `HrMetricParser` already exists in `src/HrDashboard.Agents/Models/HrMetricRow.cs` — do NOT recreate it
- SQL Server connection string key: `ConnectionStrings:DefaultConnection`
- Connection string for local dev: `Server=(localdb)\\mssqllocaldb;Database=HrDashboard;Trusted_Connection=True;`
- All new Blazor components use `@rendermode InteractiveServer` inherited from `App.razor` — do NOT add it per-component
- Identity logout: `MapGet("/account/logout", ...)` — GET is acceptable for this internal tool
- No email confirmation for registration
- No roles — all authenticated users have full access
- `ChatSessionService` is scoped (one per Blazor circuit)

---

## File Map

| Action | Path | Responsibility |
|--------|------|----------------|
| Create | `src/HrDashboard.Infrastructure/HrDashboard.Infrastructure.csproj` | New class library |
| Create | `src/HrDashboard.Infrastructure/AppUser.cs` | Identity user |
| Create | `src/HrDashboard.Infrastructure/AppDbContext.cs` | EF DbContext + Identity |
| Create | `src/HrDashboard.Infrastructure/Entities/Conversation.cs` | Conversation entity |
| Create | `src/HrDashboard.Infrastructure/Entities/Message.cs` | Message entity + enum |
| Create | `src/HrDashboard.Infrastructure/Repositories/ConversationRepository.cs` | IConversationRepository impl |
| Create | `src/HrDashboard.Agents/IConversationRepository.cs` | Repository interface (shared contract) |
| Create | `src/HrDashboard.Agents/Models/MessageRole.cs` | MessageRole enum |
| Modify | `src/HrDashboard.Agents/IHrAgentService.cs` | Add AskStreamAsync |
| Modify | `src/HrDashboard.Agents/HrAgentService.cs` | Implement AskStreamAsync + BuildMessages |
| Modify | `src/HrDashboard.Web/HrDashboard.Web.csproj` | Add packages + Infrastructure ref |
| Modify | `src/HrDashboard.Web/Program.cs` | Add Identity, EF, auth, logout, ChatSessionService DI |
| Modify | `src/HrDashboard.Web/appsettings.Development.json` | Add connection string |
| Modify | `src/HrDashboard.Web/Components/App.razor` | Add CascadingAuthenticationState |
| Modify | `src/HrDashboard.Web/Components/Routes.razor` | AuthorizeRouteView + redirect |
| Modify | `src/HrDashboard.Web/Components/_Imports.razor` | Add auth + services namespaces |
| Create | `src/HrDashboard.Web/Components/Layout/RedirectToLogin.razor` | Redirect unauthenticated users |
| Rewrite | `src/HrDashboard.Web/Components/Layout/MainLayout.razor` | 3-panel shell with toggles |
| Create | `src/HrDashboard.Web/Components/Layout/ConversationList.razor` | Left panel: history + profile |
| Create | `src/HrDashboard.Web/Components/Chat/ChatThread.razor` | Center: message bubbles |
| Create | `src/HrDashboard.Web/Components/Chat/PromptBar.razor` | Center: prompt input |
| Create | `src/HrDashboard.Web/Components/Chat/ResultsPanel.razor` | Right: chart + grid |
| Rewrite | `src/HrDashboard.Web/Components/Pages/Dashboard.razor` | Host for four components |
| Create | `src/HrDashboard.Web/Components/Pages/Auth/Login.razor` | Login page |
| Create | `src/HrDashboard.Web/Components/Pages/Auth/Register.razor` | Register page |
| Create | `src/HrDashboard.Web/Services/ChatSessionService.cs` | Scoped streaming state |
| Create | `src/HrDashboard.Web/Services/MessageViewModel.cs` | UI message model |
| Rewrite | `src/HrDashboard.Web/wwwroot/app.css` | 3-panel layout styles |

---

## Task 1: MessageRole enum + IConversationRepository interface

**Files:**
- Create: `src/HrDashboard.Agents/Models/MessageRole.cs`
- Create: `src/HrDashboard.Agents/IConversationRepository.cs`

**Interfaces:**
- Produces: `MessageRole` enum (User | Assistant), `IConversationRepository` interface with 6 methods used by Tasks 4, 8

- [ ] **Step 1: Create MessageRole enum**

`src/HrDashboard.Agents/Models/MessageRole.cs`:
```csharp
namespace HrDashboard.Agents.Models;

public enum MessageRole
{
    User,
    Assistant
}
```

- [ ] **Step 2: Create IConversationRepository**

`src/HrDashboard.Agents/IConversationRepository.cs`:
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

- [ ] **Step 3: Build the Agents project and verify it compiles**

```bash
cd c:/apps/HrDashboard
dotnet build src/HrDashboard.Agents/HrDashboard.Agents.csproj
```

Expected: Build succeeded, 0 error(s).

- [ ] **Step 4: Commit**

```bash
git add src/HrDashboard.Agents/Models/MessageRole.cs src/HrDashboard.Agents/IConversationRepository.cs
git commit -m "feat(agents): add MessageRole enum and IConversationRepository interface"
```

---

## Task 2: AskStreamAsync on IHrAgentService + HrAgentService

**Files:**
- Modify: `src/HrDashboard.Agents/IHrAgentService.cs`
- Modify: `src/HrDashboard.Agents/HrAgentService.cs`

**Interfaces:**
- Consumes: `MessageRole` (Task 1)
- Produces: `IHrAgentService.AskStreamAsync(history, prompt, ct)` → `IAsyncEnumerable<string>` used by Task 8 (ChatSessionService)

- [ ] **Step 1: Add AskStreamAsync to the interface**

Replace the contents of `src/HrDashboard.Agents/IHrAgentService.cs` with:
```csharp
using HrDashboard.Agents.Models;

namespace HrDashboard.Agents;

public interface IHrAgentService
{
    /// <summary>
    /// Submits a natural language HR analytics query and returns structured metric rows.
    /// </summary>
    Task<(string RawText, IReadOnlyList<HrMetricRow> Metrics)> AskAsync(
        string prompt,
        CancellationToken ct = default);

    /// <summary>
    /// Streams the AI response chunk by chunk. Runs the tool-use loop internally
    /// (non-streaming), then yields the final text as it arrives.
    /// history = prior (Role, Content) pairs for context — MetricsJson is never included.
    /// </summary>
    IAsyncEnumerable<string> AskStreamAsync(
        IEnumerable<(MessageRole Role, string Content)> history,
        string prompt,
        CancellationToken ct = default);
}
```

- [ ] **Step 2: Add BuildMessages helper and implement AskStreamAsync in HrAgentService**

Add the following two members to `src/HrDashboard.Agents/HrAgentService.cs` (insert before `InvokeToolAsync`):

```csharp
private List<ChatMessage> BuildMessages(
    IEnumerable<(MessageRole Role, string Content)> history,
    string prompt)
{
    var messages = new List<ChatMessage> { new(ChatRole.System, SystemPrompt) };
    foreach (var (role, content) in history)
    {
        var chatRole = role == MessageRole.User ? ChatRole.User : ChatRole.Assistant;
        messages.Add(new ChatMessage(chatRole, content));
    }
    messages.Add(new ChatMessage(ChatRole.User, prompt));
    return messages;
}

public async IAsyncEnumerable<string> AskStreamAsync(
    IEnumerable<(MessageRole Role, string Content)> history,
    string prompt,
    [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
{
    await EnsureInitializedAsync(ct);

    _logger.LogInformation("HR agent streaming prompt: {Prompt}", prompt);

    var toolOptions = new ChatOptions { Tools = [.. _tools] };
    var messages = BuildMessages(history, prompt);

    // Phase 1: tool-use loop (non-streaming) to gather Oracle HR data
    for (int i = 0; i < MaxIterations; i++)
    {
        var response = await _chatClient.GetResponseAsync(messages, toolOptions, ct);
        foreach (var msg in response.Messages)
            messages.Add(msg);

        var calls = response.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .ToList();

        if (calls.Count == 0)
        {
            // Model answered without tool calls — yield full text as single chunk
            _logger.LogInformation("Agent streaming completed in {Iterations} iteration(s)", i + 1);
            yield return response.Text ?? string.Empty;
            yield break;
        }

        if (i == MaxIterations - 1)
        {
            _logger.LogWarning("Agent streaming hit iteration limit for prompt: {Prompt}", prompt);
            yield return "[Agent reached iteration limit — rephrase your query]";
            yield break;
        }

        foreach (var call in calls)
        {
            _logger.LogDebug("Tool call: {Tool}", call.Name);
            var result = await InvokeToolAsync(call, ct);
            messages.Add(new ChatMessage(ChatRole.Tool,
                [new FunctionResultContent(call.CallId, result)]));
        }
    }

    // Phase 2: tool data gathered — stream the final summarization
    await foreach (var update in _chatClient.GetStreamingResponseAsync(messages, new ChatOptions(), ct))
    {
        if (!string.IsNullOrEmpty(update.Text))
            yield return update.Text;
    }
}
```

- [ ] **Step 3: Build Agents project and verify it compiles**

```bash
dotnet build src/HrDashboard.Agents/HrDashboard.Agents.csproj
```

Expected: Build succeeded, 0 error(s).

- [ ] **Step 4: Commit**

```bash
git add src/HrDashboard.Agents/IHrAgentService.cs src/HrDashboard.Agents/HrAgentService.cs
git commit -m "feat(agents): add AskStreamAsync with tool-use loop and streaming final response"
```

---

## Task 3: HrDashboard.Infrastructure — project skeleton + entities

**Files:**
- Create: `src/HrDashboard.Infrastructure/HrDashboard.Infrastructure.csproj`
- Create: `src/HrDashboard.Infrastructure/AppUser.cs`
- Create: `src/HrDashboard.Infrastructure/AppDbContext.cs`
- Create: `src/HrDashboard.Infrastructure/Entities/Conversation.cs`
- Create: `src/HrDashboard.Infrastructure/Entities/Message.cs`

**Interfaces:**
- Consumes: `MessageRole` (Task 1), `IConversationRepository` (Task 1)
- Produces: `AppDbContext`, `AppUser`, entity classes used by Task 4 (ConversationRepository) and Task 6 (Program.cs DI)

- [ ] **Step 1: Create the csproj**

`src/HrDashboard.Infrastructure/HrDashboard.Infrastructure.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.Identity.EntityFrameworkCore" Version="10.*" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" Version="10.*" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\HrDashboard.Agents\HrDashboard.Agents.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Add the project to the solution**

```bash
cd c:/apps/HrDashboard
dotnet sln HrDashboard.slnx add src/HrDashboard.Infrastructure/HrDashboard.Infrastructure.csproj
```

Expected: Project `src\HrDashboard.Infrastructure\HrDashboard.Infrastructure.csproj` added to the solution.

- [ ] **Step 3: Create AppUser**

`src/HrDashboard.Infrastructure/AppUser.cs`:
```csharp
using Microsoft.AspNetCore.Identity;

namespace HrDashboard.Infrastructure;

public class AppUser : IdentityUser { }
```

- [ ] **Step 4: Create Conversation entity**

`src/HrDashboard.Infrastructure/Entities/Conversation.cs`:
```csharp
namespace HrDashboard.Infrastructure.Entities;

public class Conversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = string.Empty;
    public string Title { get; set; } = "New conversation";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<Message> Messages { get; set; } = [];
}
```

- [ ] **Step 5: Create Message entity**

`src/HrDashboard.Infrastructure/Entities/Message.cs`:
```csharp
using HrDashboard.Agents.Models;

namespace HrDashboard.Infrastructure.Entities;

public class Message
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public Conversation Conversation { get; set; } = null!;
    public MessageRole Role { get; set; }
    public string Content { get; set; } = string.Empty;
    /// <summary>Serialized List&lt;HrMetricRow&gt;. Display only — never sent to agent.</summary>
    public string? MetricsJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
```

- [ ] **Step 6: Create AppDbContext**

`src/HrDashboard.Infrastructure/AppDbContext.cs`:
```csharp
using HrDashboard.Infrastructure.Entities;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace HrDashboard.Infrastructure;

public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser>(options)
{
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Conversation>(e =>
        {
            e.HasKey(c => c.Id);
            e.Property(c => c.UserId).HasMaxLength(450).IsRequired();
            e.Property(c => c.Title).HasMaxLength(512);
            e.HasMany(c => c.Messages)
             .WithOne(m => m.Conversation)
             .HasForeignKey(m => m.ConversationId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(c => c.UserId);
        });

        builder.Entity<Message>(e =>
        {
            e.HasKey(m => m.Id);
            e.Property(m => m.Content).IsRequired();
            e.Property(m => m.Role).HasConversion<string>();
        });
    }
}
```

- [ ] **Step 7: Build Infrastructure project**

```bash
dotnet build src/HrDashboard.Infrastructure/HrDashboard.Infrastructure.csproj
```

Expected: Build succeeded, 0 error(s).

- [ ] **Step 8: Commit**

```bash
git add src/HrDashboard.Infrastructure/
git commit -m "feat(infra): add Infrastructure project with AppDbContext, AppUser, and entities"
```

---

## Task 4: ConversationRepository implementation

**Files:**
- Create: `src/HrDashboard.Infrastructure/Repositories/ConversationRepository.cs`

**Interfaces:**
- Consumes: `IConversationRepository` (Task 1), `AppDbContext` (Task 3), `Conversation` + `Message` entities (Task 3)
- Produces: `ConversationRepository` class registered in DI in Task 6

- [ ] **Step 1: Create ConversationRepository**

`src/HrDashboard.Infrastructure/Repositories/ConversationRepository.cs`:
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

- [ ] **Step 2: Build Infrastructure project**

```bash
dotnet build src/HrDashboard.Infrastructure/HrDashboard.Infrastructure.csproj
```

Expected: Build succeeded, 0 error(s).

- [ ] **Step 3: Commit**

```bash
git add src/HrDashboard.Infrastructure/Repositories/ConversationRepository.cs
git commit -m "feat(infra): implement ConversationRepository with agent/display read paths"
```

---

## Task 5: EF Migration + SQL Server connection string

**Files:**
- Modify: `src/HrDashboard.Web/HrDashboard.Web.csproj` (add EF Tools)
- Modify: `src/HrDashboard.Web/appsettings.Development.json`
- Generates: `src/HrDashboard.Infrastructure/Migrations/` (EF tooling)

**Interfaces:**
- Consumes: `AppDbContext` (Task 3)
- Produces: SQL Server schema with Identity tables + Conversations + Messages

- [ ] **Step 1: Add EF Tools to the Web project (required for migration commands)**

Add inside `<ItemGroup>` in `src/HrDashboard.Web/HrDashboard.Web.csproj`:
```xml
<PackageReference Include="Microsoft.EntityFrameworkCore.Tools" Version="10.*" />
<PackageReference Include="Microsoft.AspNetCore.Identity.EntityFrameworkCore" Version="10.*" />
<PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" Version="10.*" />
```

Also add the Infrastructure project reference:
```xml
<ProjectReference Include="..\HrDashboard.Infrastructure\HrDashboard.Infrastructure.csproj" />
```

- [ ] **Step 2: Add connection string to appsettings.Development.json**

Current contents of `src/HrDashboard.Web/appsettings.Development.json` must be read first. Add `ConnectionStrings` section:
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=(localdb)\\mssqllocaldb;Database=HrDashboard;Trusted_Connection=True;MultipleActiveResultSets=true"
  }
}
```

Merge this with whatever existing keys are present — do NOT delete existing config.

- [ ] **Step 3: Temporarily register AppDbContext in Program.cs for migration scaffolding**

Before running migrations, `AppDbContext` must be registered. This will be done properly in Task 6. Add a temporary stub to `Program.cs` right before `builder.Build()`:

```csharp
// TEMP: Register AppDbContext for EF migrations — will be cleaned up in Task 6
var connStr = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:DefaultConnection");
builder.Services.AddDbContext<HrDashboard.Infrastructure.AppDbContext>(o =>
    o.UseSqlServer(connStr));
```

Also add the using at the top:
```csharp
using Microsoft.EntityFrameworkCore;
```

- [ ] **Step 4: Build to verify no compile errors**

```bash
dotnet build src/HrDashboard.Web/HrDashboard.Web.csproj
```

Expected: Build succeeded, 0 error(s).

- [ ] **Step 5: Create the EF migration**

```bash
dotnet ef migrations add InitialCreate \
  --project src/HrDashboard.Infrastructure/HrDashboard.Infrastructure.csproj \
  --startup-project src/HrDashboard.Web/HrDashboard.Web.csproj \
  --context HrDashboard.Infrastructure.AppDbContext
```

Expected: `Done. To undo this action, use 'ef migrations remove'`
Migration files appear in `src/HrDashboard.Infrastructure/Migrations/`.

- [ ] **Step 6: Apply migration to local DB**

```bash
dotnet ef database update \
  --project src/HrDashboard.Infrastructure/HrDashboard.Infrastructure.csproj \
  --startup-project src/HrDashboard.Web/HrDashboard.Web.csproj \
  --context HrDashboard.Infrastructure.AppDbContext
```

Expected: `Done.`

- [ ] **Step 7: Commit**

```bash
git add src/HrDashboard.Infrastructure/Migrations/ \
        src/HrDashboard.Web/HrDashboard.Web.csproj \
        src/HrDashboard.Web/appsettings.Development.json \
        src/HrDashboard.Web/Program.cs
git commit -m "feat(infra): add EF migration InitialCreate for Identity + Conversations + Messages"
```

---

## Task 6: Identity + auth wiring (Program.cs, App.razor, Routes.razor, _Imports.razor)

**Files:**
- Modify: `src/HrDashboard.Web/Program.cs`
- Modify: `src/HrDashboard.Web/Components/App.razor`
- Modify: `src/HrDashboard.Web/Components/Routes.razor`
- Modify: `src/HrDashboard.Web/Components/_Imports.razor`
- Create: `src/HrDashboard.Web/Components/Layout/RedirectToLogin.razor`

**Interfaces:**
- Consumes: `AppDbContext` (Task 3), `AppUser` (Task 3), `ConversationRepository` (Task 4)
- Produces: Identity middleware stack, `IConversationRepository` in DI, `[Authorize]` gate on all routes

- [ ] **Step 1: Rewrite Program.cs with full Identity + auth wiring**

Replace `src/HrDashboard.Web/Program.cs` with:
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

    // ── ChatSessionService (registered in Task 13) ───────────────────────────
    // Placeholder — Task 13 adds: builder.Services.AddScoped<ChatSessionService>();

    // ── Blazor + MudBlazor ───────────────────────────────────────────────────
    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents();
    builder.Services.AddMudServices();
    builder.Services.AddCascadingAuthenticationState();

    var app = builder.Build();

    // ── Auto-migrate on startup (dev convenience) ────────────────────────────
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

- [ ] **Step 2: Update App.razor to add CascadingAuthenticationState**

Replace `src/HrDashboard.Web/Components/App.razor` with:
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

- [ ] **Step 3: Update Routes.razor to require auth**

Replace `src/HrDashboard.Web/Components/Routes.razor` with:
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

- [ ] **Step 4: Create RedirectToLogin component**

`src/HrDashboard.Web/Components/Layout/RedirectToLogin.razor`:
```razor
@inject NavigationManager Nav

@code {
    protected override void OnInitialized()
    {
        Nav.NavigateTo("/login", forceLoad: false);
    }
}
```

- [ ] **Step 5: Update _Imports.razor with new namespaces**

Replace `src/HrDashboard.Web/Components/_Imports.razor` with:
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

- [ ] **Step 6: Build and verify**

```bash
dotnet build src/HrDashboard.Web/HrDashboard.Web.csproj
```

Expected: Build succeeded, 0 error(s).

- [ ] **Step 7: Commit**

```bash
git add src/HrDashboard.Web/Program.cs \
        src/HrDashboard.Web/Components/App.razor \
        src/HrDashboard.Web/Components/Routes.razor \
        src/HrDashboard.Web/Components/_Imports.razor \
        src/HrDashboard.Web/Components/Layout/RedirectToLogin.razor
git commit -m "feat(web): wire Identity, auth middleware, CascadingAuthenticationState, and route guard"
```

---

## Task 7: Login and Register pages

**Files:**
- Create: `src/HrDashboard.Web/Components/Pages/Auth/Login.razor`
- Create: `src/HrDashboard.Web/Components/Pages/Auth/Register.razor`

**Interfaces:**
- Consumes: `SignInManager<AppUser>`, `UserManager<AppUser>` (from DI via Task 6)
- Produces: `/login` and `/register` routes accessible to unauthenticated users

- [ ] **Step 1: Create Login page**

`src/HrDashboard.Web/Components/Pages/Auth/Login.razor`:
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

- [ ] **Step 2: Create Register page**

`src/HrDashboard.Web/Components/Pages/Auth/Register.razor`:
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

- [ ] **Step 3: Build and verify**

```bash
dotnet build src/HrDashboard.Web/HrDashboard.Web.csproj
```

Expected: Build succeeded, 0 error(s).

- [ ] **Step 4: Commit**

```bash
git add src/HrDashboard.Web/Components/Pages/Auth/
git commit -m "feat(web): add Login and Register pages with ASP.NET Core Identity"
```

---

## Task 8: ChatSessionService + MessageViewModel

**Files:**
- Create: `src/HrDashboard.Web/Services/MessageViewModel.cs`
- Create: `src/HrDashboard.Web/Services/ChatSessionService.cs`

**Interfaces:**
- Consumes: `IHrAgentService.AskStreamAsync` (Task 2), `IConversationRepository` (Task 1/4), `HrMetricParser.Parse` (already in `HrMetricRow.cs`)
- Produces: `ChatSessionService` with `OnChange`, `Messages`, `CurrentMetrics`, `SendAsync` — consumed by Tasks 10, 11, 12, 13

- [ ] **Step 1: Create MessageViewModel**

`src/HrDashboard.Web/Services/MessageViewModel.cs`:
```csharp
using HrDashboard.Agents.Models;

namespace HrDashboard.Web.Services;

public class MessageViewModel
{
    public MessageRole Role { get; init; }
    public string Content { get; set; } = string.Empty;
    public bool IsStreaming { get; set; }
    public IReadOnlyList<HrMetricRow>? Metrics { get; set; }

    public static MessageViewModel FromUser(string content) => new()
    {
        Role = MessageRole.User,
        Content = content
    };

    public static MessageViewModel StreamingAssistant() => new()
    {
        Role = MessageRole.Assistant,
        IsStreaming = true
    };
}
```

- [ ] **Step 2: Create ChatSessionService**

`src/HrDashboard.Web/Services/ChatSessionService.cs`:
```csharp
using System.Text.Json;
using HrDashboard.Agents;
using HrDashboard.Agents.Models;

namespace HrDashboard.Web.Services;

public class ChatSessionService(
    IHrAgentService agent,
    IConversationRepository repo,
    ILogger<ChatSessionService> logger)
{
    public ConversationSummary? CurrentConversation { get; private set; }
    public List<MessageViewModel> Messages { get; } = [];
    public bool IsStreaming { get; private set; }
    public IReadOnlyList<HrMetricRow> CurrentMetrics { get; private set; } = [];
    public List<ConversationSummary> Conversations { get; private set; } = [];

    public event Action? OnChange;

    public async Task LoadUserConversationsAsync(string userId, CancellationToken ct = default)
    {
        Conversations = await repo.GetByUserAsync(userId, ct);
        Notify();
    }

    public async Task StartNewConversationAsync(string userId, CancellationToken ct = default)
    {
        CurrentConversation = await repo.CreateAsync(userId, "New conversation", ct);
        Messages.Clear();
        CurrentMetrics = [];
        Conversations = await repo.GetByUserAsync(userId, ct);
        Notify();
    }

    public async Task LoadConversationAsync(Guid conversationId, string userId, CancellationToken ct = default)
    {
        var summary = Conversations.FirstOrDefault(c => c.Id == conversationId);
        if (summary is null) return;

        CurrentConversation = summary;
        Messages.Clear();
        CurrentMetrics = [];

        var rows = await repo.GetMessagesForDisplayAsync(conversationId, ct);
        foreach (var row in rows)
        {
            var vm = new MessageViewModel
            {
                Role = row.Role,
                Content = row.Content,
                Metrics = row.MetricsJson is not null
                    ? HrMetricParser.Parse(row.MetricsJson)
                    : null
            };
            Messages.Add(vm);
            if (row.Role == MessageRole.Assistant && vm.Metrics?.Count > 0)
                CurrentMetrics = vm.Metrics;
        }

        Notify();
    }

    public async Task SendAsync(string prompt, string userId, CancellationToken ct = default)
    {
        if (IsStreaming || string.IsNullOrWhiteSpace(prompt)) return;

        // Create conversation on first message
        if (CurrentConversation is null)
            await StartNewConversationAsync(userId, ct);

        var conversationId = CurrentConversation!.Id;

        // Add and persist user message
        var userVm = MessageViewModel.FromUser(prompt);
        Messages.Add(userVm);
        await repo.AddMessageAsync(conversationId, MessageRole.User, prompt, ct: ct);

        // Add streaming assistant placeholder
        var assistantVm = MessageViewModel.StreamingAssistant();
        Messages.Add(assistantVm);
        IsStreaming = true;
        CurrentMetrics = [];
        Notify();

        try
        {
            // Fetch agent context (text only — no MetricsJson)
            var history = await repo.GetMessagesForAgentAsync(conversationId, ct);

            // Stream response chunks
            await foreach (var chunk in agent.AskStreamAsync(history, prompt, ct))
            {
                assistantVm.Content += chunk;
                Notify();
            }

            // Parse metrics from completed response
            var metrics = HrMetricParser.Parse(assistantVm.Content);
            assistantVm.Metrics = metrics;
            CurrentMetrics = metrics;

            // Persist assistant message with MetricsJson
            var metricsJson = metrics.Count > 0
                ? JsonSerializer.Serialize(metrics)
                : null;
            await repo.AddMessageAsync(
                conversationId, MessageRole.Assistant, assistantVm.Content, metricsJson, ct);

            // Auto-title on first exchange
            if (Messages.Count == 2 && CurrentConversation.Title == "New conversation")
            {
                var title = prompt.Length <= 60 ? prompt : prompt[..60];
                await repo.UpdateTitleAsync(conversationId, title, ct);
                CurrentConversation = CurrentConversation with { Title = title };
                Conversations = await repo.GetByUserAsync(userId, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "ChatSessionService.SendAsync failed");
            assistantVm.Content = $"[Error: {ex.Message}]";
        }
        finally
        {
            assistantVm.IsStreaming = false;
            IsStreaming = false;
            Notify();
        }
    }

    private void Notify() => OnChange?.Invoke();
}
```

- [ ] **Step 3: Build Web project**

```bash
dotnet build src/HrDashboard.Web/HrDashboard.Web.csproj
```

Expected: Build succeeded, 0 error(s).

- [ ] **Step 4: Commit**

```bash
git add src/HrDashboard.Web/Services/
git commit -m "feat(web): add ChatSessionService and MessageViewModel for streaming conversation state"
```

---

## Task 9: MainLayout 3-panel shell + CSS

**Files:**
- Rewrite: `src/HrDashboard.Web/Components/Layout/MainLayout.razor`
- Rewrite: `src/HrDashboard.Web/wwwroot/app.css`

**Interfaces:**
- Consumes: `ConversationList.razor` (Task 10), `ChatSessionService` (Task 8)
- Produces: `_leftOpen`/`_rightOpen` state, 3-panel layout used by Tasks 10-13

- [ ] **Step 1: Write app.css**

Replace `src/HrDashboard.Web/wwwroot/app.css` with:
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

/* Panel toggle buttons */
.hr-toggle-left {
    position: absolute;
    top: 12px;
    left: 8px;
    z-index: 100;
}

.hr-toggle-right {
    position: absolute;
    top: 12px;
    right: 8px;
    z-index: 100;
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

- [ ] **Step 2: Write MainLayout.razor**

Replace `src/HrDashboard.Web/Components/Layout/MainLayout.razor` with:
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
        <ConversationList OnNewChat="NewChat" />
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
            <ResultsPanel />
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

- [ ] **Step 3: Build**

```bash
dotnet build src/HrDashboard.Web/HrDashboard.Web.csproj
```

Expected: Build succeeded (ConversationList and ResultsPanel stubs may warn until Tasks 10/12).

- [ ] **Step 4: Commit**

```bash
git add src/HrDashboard.Web/Components/Layout/MainLayout.razor \
        src/HrDashboard.Web/wwwroot/app.css
git commit -m "feat(web): 3-panel MainLayout shell with collapsible left/right panels and CSS"
```

---

## Task 10: ConversationList component

**Files:**
- Create: `src/HrDashboard.Web/Components/Layout/ConversationList.razor`

**Interfaces:**
- Consumes: `ChatSessionService` (Task 8) — `Conversations`, `CurrentConversation`, `LoadConversationAsync`, `StartNewConversationAsync`
- Produces: `ConversationList` component used by `MainLayout` (Task 9)

- [ ] **Step 1: Create ConversationList.razor**

`src/HrDashboard.Web/Components/Layout/ConversationList.razor`:
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

- [ ] **Step 2: Build**

```bash
dotnet build src/HrDashboard.Web/HrDashboard.Web.csproj
```

Expected: Build succeeded, 0 error(s).

- [ ] **Step 3: Commit**

```bash
git add src/HrDashboard.Web/Components/Layout/ConversationList.razor
git commit -m "feat(web): add ConversationList component with history and user profile"
```

---

## Task 11: ChatThread and PromptBar components

**Files:**
- Create: `src/HrDashboard.Web/Components/Chat/ChatThread.razor`
- Create: `src/HrDashboard.Web/Components/Chat/PromptBar.razor`

**Interfaces:**
- Consumes: `ChatSessionService.Messages`, `ChatSessionService.IsStreaming`, `ChatSessionService.SendAsync`
- Produces: `ChatThread` and `PromptBar` components used by `Dashboard.razor` (Task 13)

- [ ] **Step 1: Create ChatThread.razor**

`src/HrDashboard.Web/Components/Chat/ChatThread.razor`:
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
            // Scroll to bottom after render
            await Task.Yield();
        });
    }

    public void Dispose() => Session.OnChange -= OnSessionChange;
}
```

- [ ] **Step 2: Create PromptBar.razor**

`src/HrDashboard.Web/Components/Chat/PromptBar.razor`:
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

- [ ] **Step 3: Build**

```bash
dotnet build src/HrDashboard.Web/HrDashboard.Web.csproj
```

Expected: Build succeeded, 0 error(s).

- [ ] **Step 4: Commit**

```bash
git add src/HrDashboard.Web/Components/Chat/
git commit -m "feat(web): add ChatThread and PromptBar components"
```

---

## Task 12: ResultsPanel component

**Files:**
- Create: `src/HrDashboard.Web/Components/Chat/ResultsPanel.razor`

**Interfaces:**
- Consumes: `ChatSessionService.CurrentMetrics`, `ChatSessionService.IsStreaming`
- Produces: `ResultsPanel` component used by `MainLayout` (Task 9)

- [ ] **Step 1: Create ResultsPanel.razor**

`src/HrDashboard.Web/Components/Chat/ResultsPanel.razor`:
```razor
@inject ChatSessionService Session
@implements IDisposable

@if (Session.CurrentMetrics.Count == 0)
{
    <div style="display:flex;flex-direction:column;align-items:center;justify-content:center;height:200px;opacity:0.4">
        <MudIcon Icon="@Icons.Material.Filled.BarChart" Style="font-size:48px" />
        <MudText Typo="Typo.body2" Class="mt-2">Results appear here</MudText>
    </div>
}
else
{
    <MudStack Row="true" AlignItems="AlignItems.Center" Justify="Justify.SpaceBetween" Class="mb-3">
        <MudText Typo="Typo.subtitle2">Visualization</MudText>
        <MudSelect T="ChartType" @bind-Value="_chartType" Dense="true"
                   Variant="Variant.Outlined" Style="width:130px">
            <MudSelectItem Value="ChartType.Bar">Bar</MudSelectItem>
            <MudSelectItem Value="ChartType.Donut">Donut</MudSelectItem>
            <MudSelectItem Value="ChartType.Line">Line</MudSelectItem>
        </MudSelect>
    </MudStack>

    <MudChart ChartType="_chartType"
              ChartSeries="@_chartSeries"
              XAxisLabels="@_chartLabels"
              Width="100%"
              Height="220px"
              Class="mb-4" />

    <MudText Typo="Typo.subtitle2" Class="mb-2">
        Data (@Session.CurrentMetrics.Count rows)
    </MudText>
    <MudDataGrid T="HrMetricRow"
                 Items="Session.CurrentMetrics"
                 Dense="true"
                 Striped="true"
                 Hover="true"
                 Filterable="false"
                 SortMode="SortMode.Single">
        <Columns>
            <PropertyColumn Property="r => r.Label"    Title="Label"    Sortable="true" />
            <PropertyColumn Property="r => r.Value"    Title="Value"    Sortable="true" Format="N2" />
            <PropertyColumn Property="r => r.Category" Title="Category" Sortable="true" />
        </Columns>
    </MudDataGrid>
}

@code {
    private ChartType _chartType = ChartType.Bar;
    private List<ChartSeries> _chartSeries = [];
    private string[] _chartLabels = [];

    protected override void OnInitialized()
    {
        Session.OnChange += OnSessionChange;
    }

    private void OnSessionChange()
    {
        BuildChart();
        InvokeAsync(StateHasChanged);
    }

    private void BuildChart()
    {
        if (Session.CurrentMetrics.Count == 0)
        {
            _chartSeries = [];
            _chartLabels = [];
            return;
        }

        _chartLabels = Session.CurrentMetrics
            .Select(r => r.Label.Length <= 18 ? r.Label : r.Label[..18] + "…")
            .ToArray();

        _chartSeries =
        [
            new ChartSeries
            {
                Name = "Value",
                Data = Session.CurrentMetrics.Select(r => r.Value).ToArray()
            }
        ];
    }

    public void Dispose() => Session.OnChange -= OnSessionChange;
}
```

- [ ] **Step 2: Build**

```bash
dotnet build src/HrDashboard.Web/HrDashboard.Web.csproj
```

Expected: Build succeeded, 0 error(s).

- [ ] **Step 3: Commit**

```bash
git add src/HrDashboard.Web/Components/Chat/ResultsPanel.razor
git commit -m "feat(web): add ResultsPanel with MudChart and MudDataGrid driven by ChatSessionService"
```

---

## Task 13: Dashboard.razor refactor + wire DI

**Files:**
- Rewrite: `src/HrDashboard.Web/Components/Pages/Dashboard.razor`
- Modify: `src/HrDashboard.Web/Program.cs` (add `ChatSessionService` DI registration)

**Interfaces:**
- Consumes: `ChatThread`, `PromptBar` (Task 11), `ChatSessionService` (Task 8)
- Produces: The live `/` route with all panels wired

- [ ] **Step 1: Register ChatSessionService in Program.cs**

In `src/HrDashboard.Web/Program.cs`, find the placeholder comment:
```
// Placeholder — Task 13 adds: builder.Services.AddScoped<ChatSessionService>();
```

Replace that comment line with:
```csharp
builder.Services.AddScoped<HrDashboard.Web.Services.ChatSessionService>();
```

- [ ] **Step 2: Rewrite Dashboard.razor**

Replace `src/HrDashboard.Web/Components/Pages/Dashboard.razor` with:
```razor
@page "/"
@attribute [Microsoft.AspNetCore.Authorization.Authorize]

<PageTitle>HR Analytics</PageTitle>

<div style="display:flex;flex-direction:column;height:100%">
    <ChatThread />
    <PromptBar />
</div>
```

- [ ] **Step 3: Build**

```bash
dotnet build src/HrDashboard.Web/HrDashboard.Web.csproj
```

Expected: Build succeeded, 0 error(s).

- [ ] **Step 4: Run the app and smoke-test**

```bash
dotnet run --project src/HrDashboard.Web/HrDashboard.Web.csproj
```

Manual checks:
1. Browser opens → redirected to `/login`
2. Click "Register" → create an account → redirected to `/`
3. Three-panel layout visible
4. Left panel toggle collapses/expands sidebar
5. Right panel toggle hides/shows results panel
6. Type a prompt and press Ctrl+Enter or click Send
7. Response streams into center panel
8. Chart and grid appear in right panel
9. Refresh page → conversation persists and loads from DB
10. Sign out → redirected to `/login`

- [ ] **Step 5: Commit**

```bash
git add src/HrDashboard.Web/Components/Pages/Dashboard.razor \
        src/HrDashboard.Web/Program.cs
git commit -m "feat(web): refactor Dashboard to host ChatThread+PromptBar; register ChatSessionService"
```

---

## Self-Review

**Spec coverage check:**

| Spec requirement | Task(s) |
|-----------------|---------|
| 3-panel layout with toggles | Task 9 (MainLayout) |
| Left panel: chat history + profile/logout | Task 10 (ConversationList) |
| Center: streaming chat thread | Task 11 (ChatThread, PromptBar) |
| Right: chart + grid | Task 12 (ResultsPanel) |
| SQL Server persistence | Tasks 3, 4, 5 |
| ASP.NET Core Identity login/register | Tasks 6, 7 |
| Clean architecture (Infrastructure project) | Tasks 3, 4 |
| AskStreamAsync with tool-use loop | Task 2 |
| MetricsJson never sent to agent | Task 8 (ChatSessionService uses GetMessagesForAgentAsync) |
| Auto-title on first exchange | Task 8 (ChatSessionService.SendAsync) |
| IConversationRepository.GetMessagesForAgentAsync returns text only | Task 1 (interface) + Task 4 (impl) |
| EF migration applied at startup | Task 6 (Program.cs db.Database.MigrateAsync) |
| Right panel placeholder when no metrics | Task 12 |
| Preset chips | Task 11 (PromptBar) |
| MessageViewModel with IsStreaming | Task 8 |

All spec requirements covered. No gaps found.

**Type consistency check:** `ConversationSummary`, `MessageDisplay`, `MessageRole`, `ChatSessionService`, `MessageViewModel` — names consistent across Tasks 1, 4, 8, 10, 11, 12, 13. ✓

**Placeholder scan:** No TBD/TODO in any code block. ✓

# Chat Token/Cost Usage Logging Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Passively log token usage per chat turn (tagged by provider/model), persist it per user, and show it via a usage dashboard dialog with a daily-tokens chart and a provider/model breakdown table.

**Architecture:** `HrAgentService.AskStreamAsync` accumulates `Microsoft.Extensions.AI.UsageDetails` across every internal LLM call in one user turn (each Phase 1 tool-round plus Phase 2's streaming final answer) into a new `LastTurnUsage` property, following the exact same "stateful property read once the stream drains" contract already established for `LastPendingColumnOptions`. `ChatSessionService.SendAsync` reads it after persisting the assistant message and writes a new `UsageRecord` row (tied to that message) via a new `IUsageRepository`. A new `UsageDashboard.razor` component — shown as a full-screen dialog (this app's only existing pattern for secondary views; it has never had a second page/route) — reads the aggregated totals back out.

**Tech Stack:** .NET 10, Blazor Server, Microsoft.Extensions.AI 10.9.0, EF Core (SQL Server + InMemory for tests), MudBlazor 8.15.0, xUnit + FluentAssertions + NSubstitute.

**Spec:** docs/superpowers/specs/2026-08-24-chat-usage-logging-design.md

## Global Constraints

- GitFlow branching: this work happens on `feature/chat-usage-logging`, branched off `develop`. Any bug found and fixed mid-implementation branches as `bugfix/<description>`, never `fix/<description>`.
- Do not include `Co-Authored-By: Claude ... <noreply@anthropic.com>` in commit messages (project preference, see `.wolf/cerebrum.md`).
- No dollar-cost estimation, no active budget enforcement, no admin cross-user view, no date-range picker — see the spec's Non-Goals. `UserId` is still stored on every record so an admin view remains possible later without a schema change.
- The usage dashboard is a full-screen `DialogService` dialog, not a new `@page` route — `Dashboard.razor` is this app's only route today.
- `src/HrDashboard.Web` has no xUnit test project — Web-layer changes are verified live, matching established project practice.
- A turn where the provider didn't report usage produces no `UsageRecord` — this is a passive log, not a requirement every turn must satisfy.

---

### Task 1: `TurnUsageInfo` model + widen `HrAgentService`'s constructor

**Files:**
- Create: `src/HrDashboard.Agents/Models/TurnUsageInfo.cs`
- Modify: `src/HrDashboard.Agents/IHrAgentService.cs`
- Modify: `src/HrDashboard.Agents/HrAgentService.cs` (fields, constructors, new stub property — no accumulation logic yet, that's Task 2)
- Modify: `src/HrDashboard.Web/Program.cs` (pass provider/model into the constructor)

**Interfaces:**
- Produces: `HrDashboard.Agents.Models.TurnUsageInfo(string Provider, string Model, long InputTokens, long OutputTokens, long TotalTokens)`. `IHrAgentService.LastTurnUsage` property (`TurnUsageInfo?`, populated by Task 2). `HrAgentService`'s public constructor gains two new required `string` parameters (`providerName`, `modelName`) inserted between the existing `mcpServerEndpoint` and `logger` parameters. The internal test-only constructor gains the same two parameters as *optional*, defaulting to `"TestProvider"`/`"test-model"`, so every existing call site (`Build(client)` in `HrAgentServiceTests.cs`) keeps compiling unchanged.

- [ ] **Step 1: Create the feature branch**

```bash
git -C c:/apps/HrDashboard checkout develop
git -C c:/apps/HrDashboard pull
git -C c:/apps/HrDashboard checkout -b feature/chat-usage-logging
```

- [ ] **Step 2: Create `TurnUsageInfo`**

Create `src/HrDashboard.Agents/Models/TurnUsageInfo.cs`:

```csharp
namespace HrDashboard.Agents.Models;

/// <summary>
/// Accumulated token usage for one user-visible chat turn — summed across every
/// internal LLM call that turn triggered (tool-loop rounds plus the final streamed
/// answer), tagged with the provider/model that produced it. Built from whatever the
/// underlying Microsoft.Extensions.AI provider actually reports; a provider that
/// doesn't report usage for a given call simply contributes nothing to the total.
/// </summary>
public sealed record TurnUsageInfo(
    string Provider,
    string Model,
    long InputTokens,
    long OutputTokens,
    long TotalTokens);
```

- [ ] **Step 3: Widen `IHrAgentService`**

In `src/HrDashboard.Agents/IHrAgentService.cs`, add this member to the interface, right after `LastPendingColumnOptions`:

```csharp

    /// <summary>
    /// Set once the most recent <see cref="AskStreamAsync"/> call's stream is fully
    /// drained. Null if the underlying provider reported no usage at all for that
    /// turn's LLM calls; otherwise the summed input/output/total token counts across
    /// every internal call the turn made.
    /// </summary>
    TurnUsageInfo? LastTurnUsage { get; }
```

- [ ] **Step 4: Widen `HrAgentService`'s fields, property, and constructors**

In `src/HrDashboard.Agents/HrAgentService.cs`, replace the field declarations block:

```csharp
    private readonly IChatClient _chatClient;
    private readonly string _mcpServerEndpoint;
    private readonly ILogger<HrAgentService> _logger;
```

with:

```csharp
    private readonly IChatClient _chatClient;
    private readonly string _mcpServerEndpoint;
    private readonly string _providerName;
    private readonly string _modelName;
    private readonly ILogger<HrAgentService> _logger;
```

Add this property right after the existing `LastPendingColumnOptions` property declaration:

```csharp
    public TurnUsageInfo? LastTurnUsage { get; private set; }
```

Replace both constructors:

```csharp
    public HrAgentService(IChatClient chatClient, string mcpServerEndpoint, ILogger<HrAgentService> logger)
    {
        _chatClient        = chatClient;
        _mcpServerEndpoint = mcpServerEndpoint;
        _logger            = logger;
    }

    /// <summary>Test-only constructor — bypasses MCP HTTP initialization.</summary>
    internal HrAgentService(
        IChatClient chatClient,
        IList<AITool> tools,
        ILogger<HrAgentService> logger)
    {
        _chatClient        = chatClient;
        _mcpServerEndpoint = string.Empty;
        _logger            = logger;
        _tools             = tools;
        _initialized       = true;
    }
```

with:

```csharp
    public HrAgentService(
        IChatClient chatClient,
        string mcpServerEndpoint,
        string providerName,
        string modelName,
        ILogger<HrAgentService> logger)
    {
        _chatClient        = chatClient;
        _mcpServerEndpoint = mcpServerEndpoint;
        _providerName      = providerName;
        _modelName         = modelName;
        _logger            = logger;
    }

    /// <summary>Test-only constructor — bypasses MCP HTTP initialization.</summary>
    internal HrAgentService(
        IChatClient chatClient,
        IList<AITool> tools,
        ILogger<HrAgentService> logger,
        string providerName = "TestProvider",
        string modelName = "test-model")
    {
        _chatClient        = chatClient;
        _mcpServerEndpoint = string.Empty;
        _providerName      = providerName;
        _modelName         = modelName;
        _logger            = logger;
        _tools             = tools;
        _initialized       = true;
    }
```

- [ ] **Step 5: Update `Program.cs` to pass provider/model into the constructor**

In `src/HrDashboard.Web/Program.cs`, the `resolvedModel` value is currently computed AFTER `builder.Build()`, but `HrAgentService`'s DI registration (which needs it) runs BEFORE that. Move the computation earlier.

Replace:

```csharp
    // ── IChatClient ──────────────────────────────────────────────────────────
    var provider = builder.Configuration["AI:Provider"] ?? "Ollama";

    builder.Services.AddSingleton<IChatClient>(_ =>
```

with:

```csharp
    // ── IChatClient ──────────────────────────────────────────────────────────
    var provider = builder.Configuration["AI:Provider"] ?? "Ollama";

    // Resolved once, here, so both the IChatClient factory's model selection below and
    // HrAgentService's usage-tracking label use the exact same value — computed before
    // Build() since HrAgentService's DI registration below needs it.
    var resolvedModel = provider switch
    {
        var p when string.Equals(p, "AzureOpenAI", StringComparison.OrdinalIgnoreCase) =>
            builder.Configuration["AI:AzureOpenAI:DeploymentName"] ?? "gpt-4o",
        var p when string.Equals(p, "Nvidia", StringComparison.OrdinalIgnoreCase) =>
            builder.Configuration["AI:Nvidia:Model"] ?? "meta/llama-3.1-8b-instruct",
        _ => builder.Configuration["AI:Ollama:Model"] ?? "llama3.1"
    };

    builder.Services.AddSingleton<IChatClient>(_ =>
```

Then replace:

```csharp
    builder.Services.AddScoped<IHrAgentService>(sp =>
    {
        var chatClient = sp.GetRequiredService<IChatClient>();
        var endpoint   = builder.Configuration["McpServer:Endpoint"] ?? "http://localhost:5200/mcp";
        var logger     = sp.GetRequiredService<ILogger<HrAgentService>>();
        return new HrAgentService(chatClient, endpoint, logger);
    });
```

with:

```csharp
    builder.Services.AddScoped<IHrAgentService>(sp =>
    {
        var chatClient = sp.GetRequiredService<IChatClient>();
        var endpoint   = builder.Configuration["McpServer:Endpoint"] ?? "http://localhost:5200/mcp";
        var logger     = sp.GetRequiredService<ILogger<HrAgentService>>();
        return new HrAgentService(chatClient, endpoint, provider, resolvedModel, logger);
    });
```

Finally, replace the later duplicate computation (after `var app = builder.Build();`):

```csharp
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
```

with just:

```csharp
    // ── Log active AI provider/model so it's visible without digging through config ──
    Log.Information("AI provider: {Provider} | Model: {Model}", provider, resolvedModel);
```

- [ ] **Step 6: Build and run the full Agents test suite to confirm no regression**

```bash
dotnet build HrDashboard.slnx
dotnet test tests/HrDashboard.Agents.Tests
```

Expected: build succeeds; all existing tests still pass unchanged (the test-only constructor's new parameters are optional, so `Build(client)` in `HrAgentServiceTests.cs` compiles as-is).

- [ ] **Step 7: Commit**

```bash
git add src/HrDashboard.Agents/Models/TurnUsageInfo.cs src/HrDashboard.Agents/IHrAgentService.cs src/HrDashboard.Agents/HrAgentService.cs src/HrDashboard.Web/Program.cs
git commit -m "feat: widen HrAgentService with provider/model label and LastTurnUsage stub"
```

---

### Task 2: Accumulate token usage in `AskStreamAsync`

**Files:**
- Modify: `src/HrDashboard.Agents/HrAgentService.cs` (`AskStreamAsync` method)
- Modify: `tests/HrDashboard.Agents.Tests/HrAgentServiceTests.cs`

**Interfaces:**
- Consumes: `Microsoft.Extensions.AI.UsageDetails` (`InputTokenCount`, `OutputTokenCount`, `TotalTokenCount` — all `long?`) via `ChatResponse.Usage` (Phase 1) and `Microsoft.Extensions.AI.UsageContent.Details` found among `ChatResponseUpdate.Contents` (Phase 2 streaming).
- Produces: `HrAgentService.LastTurnUsage` is now meaningfully populated after every exit point of `AskStreamAsync` (the no-tools-at-all early return, the iteration-limit early return, and the normal Phase 2 completion) — not just the happy path, since real tokens were consumed even on a turn that ultimately failed.

- [ ] **Step 1: Write the failing tests**

Add to `tests/HrDashboard.Agents.Tests/HrAgentServiceTests.cs`, in the "AskStreamAsync tests" region. First add a new helper alongside the existing `FakeStream` helper (do not remove `FakeStream` — both are used):

```csharp
    // Helper: fake IAsyncEnumerable<ChatResponseUpdate> from full updates (not just text),
    // for tests that need to include non-text content like UsageContent.
    private static async IAsyncEnumerable<ChatResponseUpdate> FakeStreamUpdates(
        params ChatResponseUpdate[] updates)
    {
        foreach (var update in updates)
        {
            yield return update;
            await Task.CompletedTask;
        }
    }
```

Then add these tests:

```csharp
    [Fact]
    public async Task AskStreamAsync_AccumulatesUsageAcrossPhase1AndPhase2()
    {
        var client = Substitute.For<IChatClient>();

        var toolCallResponseWithUsage = new ChatResponse(
            [new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", "GetData", null)])])
        {
            Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 20, TotalTokenCount = 120 }
        };
        var finalResponseWithUsage = new ChatResponse([new ChatMessage(ChatRole.Assistant, string.Empty)])
        {
            Usage = new UsageDetails { InputTokenCount = 50, OutputTokenCount = 10, TotalTokenCount = 60 }
        };

        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(toolCallResponseWithUsage), Task.FromResult(finalResponseWithUsage));

        client.GetStreamingResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
              .Returns(FakeStreamUpdates(
                  new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("Hello ")]),
                  new ChatResponseUpdate(ChatRole.Assistant,
                      [new TextContent("world"),
                       new UsageContent(new UsageDetails { InputTokenCount = 200, OutputTokenCount = 30, TotalTokenCount = 230 })])));

        var sut = new HrAgentService(client, [], NullLogger<HrAgentService>.Instance, "TestProvider", "test-model");

        await foreach (var _ in sut.AskStreamAsync([], "query")) { }

        sut.LastTurnUsage.Should().NotBeNull();
        sut.LastTurnUsage!.Provider.Should().Be("TestProvider");
        sut.LastTurnUsage.Model.Should().Be("test-model");
        sut.LastTurnUsage.InputTokens.Should().Be(100 + 50 + 200);
        sut.LastTurnUsage.OutputTokens.Should().Be(20 + 10 + 30);
        sut.LastTurnUsage.TotalTokens.Should().Be(120 + 60 + 230);
    }

    [Fact]
    public async Task AskStreamAsync_NoUsageReportedAnywhere_LeavesLastTurnUsageNull()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(TextResponse("Hello world")));

        var sut = Build(client);
        await foreach (var _ in sut.AskStreamAsync([], "hi")) { }

        sut.LastTurnUsage.Should().BeNull();
    }

    [Fact]
    public async Task AskStreamAsync_IterationLimit_StillReportsUsageFromRoundsThatRan()
    {
        var client = Substitute.For<IChatClient>();
        var loopingResponseWithUsage = new ChatResponse(
            [new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", "loop", null)])])
        {
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5, TotalTokenCount = 15 }
        };
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
              .Returns(_ => Task.FromResult(loopingResponseWithUsage));

        var sut = Build(client);
        await foreach (var _ in sut.AskStreamAsync([], "loop forever")) { }

        sut.LastTurnUsage.Should().NotBeNull();
        sut.LastTurnUsage!.TotalTokens.Should().BeGreaterThan(0);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/HrDashboard.Agents.Tests --filter "FullyQualifiedName~AccumulatesUsage|FullyQualifiedName~NoUsageReportedAnywhere|FullyQualifiedName~IterationLimit_StillReportsUsage"`
Expected: FAIL — `LastTurnUsage` stays null in the first and third tests (accumulation logic doesn't exist yet).

- [ ] **Step 3: Implement usage accumulation in `AskStreamAsync`**

In `src/HrDashboard.Agents/HrAgentService.cs`, replace the entire `AskStreamAsync` method with:

```csharp
    public async IAsyncEnumerable<string> AskStreamAsync(
        IEnumerable<(MessageRole Role, string Content)> history,
        string prompt,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        LastPendingColumnOptions = null;
        LastTurnUsage = null;
        _logger.LogInformation("HR agent streaming prompt: {Prompt}", prompt);

        var totalStopwatch = Stopwatch.StartNew();
        // AllowMultipleToolCalls=false — see the identical setting in AskAsync for why.
        var toolOptions = new ChatOptions { Tools = [.. _tools], AllowMultipleToolCalls = false };
        var messages = BuildMessages(history, prompt);
        bool toolsWereUsed = false;
        var tracker = new ToolCallTracker();

        long totalInputTokens = 0, totalOutputTokens = 0, totalTokens = 0;
        bool anyUsageSeen = false;

        TurnUsageInfo? FinalizeUsage() =>
            anyUsageSeen ? new TurnUsageInfo(_providerName, _modelName, totalInputTokens, totalOutputTokens, totalTokens) : null;

        // Phase 1: tool-use loop (non-streaming) to gather Oracle HR data.
        // We do NOT add the final no-tool-call response to messages; Phase 2 streams it.
        for (int i = 0; i < MaxIterations; i++)
        {
            var roundStopwatch = Stopwatch.StartNew();
            var response = await _chatClient.GetResponseAsync(messages, toolOptions, ct);
            _logger.LogInformation("Phase 1 round {Round}: model call took {ElapsedMs}ms", i, roundStopwatch.ElapsedMilliseconds);

            if (response.Usage is { } roundUsage)
            {
                anyUsageSeen = true;
                totalInputTokens  += roundUsage.InputTokenCount ?? 0;
                totalOutputTokens += roundUsage.OutputTokenCount ?? 0;
                totalTokens       += roundUsage.TotalTokenCount ?? 0;
            }

            var calls = response.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .ToList();

            if (calls.Count == 0)
            {
                if (!toolsWereUsed)
                {
                    // Model answered without any tool calls — yield text directly (no extra API call)
                    _logger.LogInformation("Agent streaming (no tools) completed in 1 round, {ElapsedMs}ms total", totalStopwatch.ElapsedMilliseconds);
                    LastTurnUsage = FinalizeUsage();
                    yield return response.Text ?? string.Empty;
                    yield break;
                }

                // Tool data is in messages; fall through to Phase 2 for streaming final answer
                _logger.LogInformation(
                    "Agent tool-use loop done after {Rounds} round(s) in {ElapsedMs}ms, streaming final answer",
                    i, totalStopwatch.ElapsedMilliseconds);
                break;
            }

            toolsWereUsed = true;

            if (i == MaxIterations - 1)
            {
                _logger.LogWarning("Agent streaming hit iteration limit for prompt: {Prompt}", prompt);
                LastTurnUsage = FinalizeUsage();
                yield return "[Agent reached iteration limit — rephrase your query]";
                yield break;
            }

            // Add tool-call messages and results; final answer is never added here
            foreach (var msg in response.Messages)
                messages.Add(msg);

            foreach (var call in calls)
            {
                var toolStopwatch = Stopwatch.StartNew();
                var result = await InvokeToolAsync(call, ct);
                tracker.Observe(call, result);
                _logger.LogInformation("Tool call {Tool} took {ElapsedMs}ms", call.Name, toolStopwatch.ElapsedMilliseconds);
                messages.Add(new ChatMessage(ChatRole.Tool,
                    [new FunctionResultContent(call.CallId, result)]));
            }
        }

        // Phase 2: stream the final summarization over the accumulated tool context
        var phase2Stopwatch = Stopwatch.StartNew();
        var chunkCount = 0;
        var fullTextBuilder = new StringBuilder();
        await foreach (var update in _chatClient.GetStreamingResponseAsync(messages, new ChatOptions(), ct))
        {
            foreach (var usageContent in update.Contents.OfType<UsageContent>())
            {
                anyUsageSeen = true;
                totalInputTokens  += usageContent.Details.InputTokenCount ?? 0;
                totalOutputTokens += usageContent.Details.OutputTokenCount ?? 0;
                totalTokens       += usageContent.Details.TotalTokenCount ?? 0;
            }

            if (!string.IsNullOrEmpty(update.Text))
            {
                chunkCount++;
                fullTextBuilder.Append(update.Text);
                yield return update.Text;
            }
        }

        LastPendingColumnOptions = tracker.Classify(fullTextBuilder.ToString());
        LastTurnUsage = FinalizeUsage();

        _logger.LogInformation(
            "Agent streaming final answer complete: {ChunkCount} chunk(s), phase 2 took {Phase2Ms}ms, {TotalMs}ms total",
            chunkCount, phase2Stopwatch.ElapsedMilliseconds, totalStopwatch.ElapsedMilliseconds);
    }
```

- [ ] **Step 4: Run all Agents tests to verify they pass**

Run: `dotnet test tests/HrDashboard.Agents.Tests`
Expected: PASS (all tests in the project)

- [ ] **Step 5: Commit**

```bash
git add src/HrDashboard.Agents/HrAgentService.cs tests/HrDashboard.Agents.Tests/HrAgentServiceTests.cs
git commit -m "feat: accumulate token usage across AskStreamAsync's internal LLM calls"
```

---

### Task 3: `UsageRecord` entity + `AppDbContext` + migration

**Files:**
- Create: `src/HrDashboard.Infrastructure/Entities/UsageRecord.cs`
- Modify: `src/HrDashboard.Infrastructure/AppDbContext.cs`
- Create: new EF migration under `src/HrDashboard.Infrastructure/Migrations/` (generated by the CLI, not hand-written)

**Interfaces:**
- Produces: `HrDashboard.Infrastructure.Entities.UsageRecord` — `Id (Guid)`, `MessageId (Guid, FK -> Message)`, `Message (Message navigation)`, `UserId (string)`, `Provider (string)`, `Model (string)`, `InputTokens (long)`, `OutputTokens (long)`, `TotalTokens (long)`, `CreatedAt (DateTime, defaults to DateTime.UtcNow)`. `AppDbContext.UsageRecords` (`DbSet<UsageRecord>`).

- [ ] **Step 1: Create the `UsageRecord` entity**

Create `src/HrDashboard.Infrastructure/Entities/UsageRecord.cs`:

```csharp
namespace HrDashboard.Infrastructure.Entities;

public class UsageRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MessageId { get; set; }
    public Message Message { get; set; } = null!;
    public string UserId { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long TotalTokens { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
```

- [ ] **Step 2: Add the `DbSet` and entity configuration**

In `src/HrDashboard.Infrastructure/AppDbContext.cs`, replace:

```csharp
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();
```

with:

```csharp
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<UsageRecord> UsageRecords => Set<UsageRecord>();
```

Then, inside `OnModelCreating`, add this block right after the existing `builder.Entity<Message>(e => { ... });` block:

```csharp

        builder.Entity<UsageRecord>(e =>
        {
            e.HasKey(u => u.Id);
            e.Property(u => u.UserId).HasMaxLength(450).IsRequired();
            e.Property(u => u.Provider).HasMaxLength(100).IsRequired();
            e.Property(u => u.Model).HasMaxLength(200).IsRequired();
            e.HasOne(u => u.Message)
             .WithMany()
             .HasForeignKey(u => u.MessageId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(u => u.UserId);
            e.HasIndex(u => u.CreatedAt);
        });
```

- [ ] **Step 3: Build**

```bash
dotnet build src/HrDashboard.Infrastructure/HrDashboard.Infrastructure.csproj
```

Expected: Build succeeded, 0 error(s).

- [ ] **Step 4: Create the EF migration**

```bash
dotnet ef migrations add AddUsageRecords \
  --project src/HrDashboard.Infrastructure/HrDashboard.Infrastructure.csproj \
  --startup-project src/HrDashboard.Web/HrDashboard.Web.csproj \
  --context HrDashboard.Infrastructure.AppDbContext
```

Expected: `Done. To undo this action, use 'ef migrations remove'`. New migration files appear in `src/HrDashboard.Infrastructure/Migrations/`.

- [ ] **Step 5: Commit**

```bash
git add src/HrDashboard.Infrastructure/Entities/UsageRecord.cs src/HrDashboard.Infrastructure/AppDbContext.cs src/HrDashboard.Infrastructure/Migrations/
git commit -m "feat: add UsageRecord entity and migration"
```

Note: the migration is *applied* automatically on next app startup (`Program.cs` already runs `db.Database.MigrateAsync()`), so no manual `dotnet ef database update` step is required — Task 7's live verification will apply it.

---

### Task 4: `IUsageRepository` + implementation + tests

**Files:**
- Create: `src/HrDashboard.Agents/IUsageRepository.cs`
- Create: `src/HrDashboard.Infrastructure/Repositories/UsageRepository.cs`
- Test: `tests/HrDashboard.Infrastructure.Tests/UsageRepositoryTests.cs`

**Interfaces:**
- Consumes: `HrDashboard.Infrastructure.Entities.UsageRecord` (Task 3). `TestDbContextFactory` (existing, in `tests/HrDashboard.Infrastructure.Tests/TestDbContextFactory.cs`) and `ConversationRepository` (existing) for test setup.
- Produces: `HrDashboard.Agents.IUsageRepository` with `AddUsageRecordAsync(Guid messageId, string userId, string provider, string model, long inputTokens, long outputTokens, long totalTokens, CancellationToken ct = default) -> Task`, `GetDailyTotalsAsync(string userId, int days, CancellationToken ct = default) -> Task<List<DailyUsageTotal>>`, `GetProviderModelBreakdownAsync(string userId, CancellationToken ct = default) -> Task<List<ProviderModelUsageTotal>>`. `HrDashboard.Agents.DailyUsageTotal(DateOnly Date, long TotalTokens)`. `HrDashboard.Agents.ProviderModelUsageTotal(string Provider, string Model, long InputTokens, long OutputTokens, long TotalTokens, int TurnCount)`. `HrDashboard.Infrastructure.Repositories.UsageRepository(IDbContextFactory<AppDbContext> dbFactory)` implementing it.

- [ ] **Step 1: Write the failing tests**

Create `tests/HrDashboard.Infrastructure.Tests/UsageRepositoryTests.cs`:

```csharp
using FluentAssertions;
using HrDashboard.Agents;
using HrDashboard.Agents.Models;
using HrDashboard.Infrastructure.Entities;
using HrDashboard.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HrDashboard.Infrastructure.Tests;

public class UsageRepositoryTests
{
    private static async Task<Guid> CreateMessageAsync(TestDbContextFactory factory, string userId)
    {
        var convRepo = new ConversationRepository(factory);
        var conv = await convRepo.CreateAsync(userId, "Chat");
        var msg  = await convRepo.AddMessageAsync(conv.Id, MessageRole.Assistant, "Answer");
        return msg.Id;
    }

    [Fact]
    public async Task AddUsageRecordAsync_PersistsRecord()
    {
        var factory    = new TestDbContextFactory();
        var messageId  = await CreateMessageAsync(factory, "user-a");
        var repo       = new UsageRepository(factory);

        await repo.AddUsageRecordAsync(
            messageId, "user-a", "Nvidia", "meta/llama-3.1-8b-instruct", 100, 20, 120);

        var breakdown = await repo.GetProviderModelBreakdownAsync("user-a");
        breakdown.Should().ContainSingle();
        breakdown[0].Provider.Should().Be("Nvidia");
        breakdown[0].InputTokens.Should().Be(100);
        breakdown[0].OutputTokens.Should().Be(20);
        breakdown[0].TotalTokens.Should().Be(120);
        breakdown[0].TurnCount.Should().Be(1);
    }

    [Fact]
    public async Task GetProviderModelBreakdownAsync_OnlyReturnsCallerRecords()
    {
        var factory  = new TestDbContextFactory();
        var repo     = new UsageRepository(factory);
        var messageA = await CreateMessageAsync(factory, "user-a");
        var messageB = await CreateMessageAsync(factory, "user-b");

        await repo.AddUsageRecordAsync(messageA, "user-a", "Ollama", "qwen3-vl:8b", 10, 5, 15);
        await repo.AddUsageRecordAsync(messageB, "user-b", "Ollama", "qwen3-vl:8b", 999, 999, 1998);

        var breakdown = await repo.GetProviderModelBreakdownAsync("user-a");

        breakdown.Should().ContainSingle();
        breakdown[0].TotalTokens.Should().Be(15);
    }

    [Fact]
    public async Task GetProviderModelBreakdownAsync_SumsAcrossMultipleTurnsForSameProviderModel()
    {
        var factory  = new TestDbContextFactory();
        var repo     = new UsageRepository(factory);
        var userId   = "user-c";
        var message1 = await CreateMessageAsync(factory, userId);
        var message2 = await CreateMessageAsync(factory, userId);

        await repo.AddUsageRecordAsync(message1, userId, "Nvidia", "meta/llama-3.1-8b-instruct", 100, 20, 120);
        await repo.AddUsageRecordAsync(message2, userId, "Nvidia", "meta/llama-3.1-8b-instruct", 50, 10, 60);

        var breakdown = await repo.GetProviderModelBreakdownAsync(userId);

        breakdown.Should().ContainSingle();
        breakdown[0].InputTokens.Should().Be(150);
        breakdown[0].OutputTokens.Should().Be(30);
        breakdown[0].TotalTokens.Should().Be(180);
        breakdown[0].TurnCount.Should().Be(2);
    }

    [Fact]
    public async Task GetDailyTotalsAsync_SumsMultipleRecordsOnTheSameDay()
    {
        var factory  = new TestDbContextFactory();
        var repo     = new UsageRepository(factory);
        var userId   = "user-e";
        var message1 = await CreateMessageAsync(factory, userId);
        var message2 = await CreateMessageAsync(factory, userId);

        await repo.AddUsageRecordAsync(message1, userId, "Ollama", "qwen3-vl:8b", 10, 10, 20);
        await repo.AddUsageRecordAsync(message2, userId, "Ollama", "qwen3-vl:8b", 5, 5, 10);

        var totals = await repo.GetDailyTotalsAsync(userId, days: 30);

        totals.Should().ContainSingle();
        totals[0].TotalTokens.Should().Be(30);
    }

    [Fact]
    public async Task GetDailyTotalsAsync_ExcludesRecordsOutsideTheRequestedWindow()
    {
        var factory              = new TestDbContextFactory();
        var repo                 = new UsageRepository(factory);
        var userId               = "user-d";
        var messageInWindow      = await CreateMessageAsync(factory, userId);
        var messageOutsideWindow = await CreateMessageAsync(factory, userId);

        await using (var db = factory.CreateDbContext())
        {
            db.UsageRecords.Add(new UsageRecord
            {
                MessageId = messageInWindow, UserId = userId, Provider = "Ollama", Model = "qwen3-vl:8b",
                InputTokens = 10, OutputTokens = 10, TotalTokens = 20, CreatedAt = DateTime.UtcNow
            });
            db.UsageRecords.Add(new UsageRecord
            {
                MessageId = messageOutsideWindow, UserId = userId, Provider = "Ollama", Model = "qwen3-vl:8b",
                InputTokens = 999, OutputTokens = 999, TotalTokens = 1998, CreatedAt = DateTime.UtcNow.AddDays(-60)
            });
            await db.SaveChangesAsync();
        }

        var totals = await repo.GetDailyTotalsAsync(userId, days: 30);

        totals.Should().ContainSingle();
        totals[0].TotalTokens.Should().Be(20);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/HrDashboard.Infrastructure.Tests --filter "FullyQualifiedName~UsageRepositoryTests"`
Expected: FAIL to build — `IUsageRepository`/`UsageRepository` don't exist yet.

- [ ] **Step 3: Create `IUsageRepository`**

Create `src/HrDashboard.Agents/IUsageRepository.cs`:

```csharp
namespace HrDashboard.Agents;

public interface IUsageRepository
{
    Task AddUsageRecordAsync(
        Guid messageId,
        string userId,
        string provider,
        string model,
        long inputTokens,
        long outputTokens,
        long totalTokens,
        CancellationToken ct = default);

    /// <summary>Total tokens per calendar day, over the last <paramref name="days"/> days, oldest first.</summary>
    Task<List<DailyUsageTotal>> GetDailyTotalsAsync(
        string userId, int days, CancellationToken ct = default);

    /// <summary>All-time totals grouped by provider + model.</summary>
    Task<List<ProviderModelUsageTotal>> GetProviderModelBreakdownAsync(
        string userId, CancellationToken ct = default);
}

public record DailyUsageTotal(DateOnly Date, long TotalTokens);

public record ProviderModelUsageTotal(
    string Provider,
    string Model,
    long InputTokens,
    long OutputTokens,
    long TotalTokens,
    int TurnCount);
```

- [ ] **Step 4: Implement `UsageRepository`**

Create `src/HrDashboard.Infrastructure/Repositories/UsageRepository.cs`:

```csharp
using HrDashboard.Agents;
using HrDashboard.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;

namespace HrDashboard.Infrastructure.Repositories;

public class UsageRepository(IDbContextFactory<AppDbContext> dbFactory) : IUsageRepository
{
    public async Task AddUsageRecordAsync(
        Guid messageId,
        string userId,
        string provider,
        string model,
        long inputTokens,
        long outputTokens,
        long totalTokens,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.UsageRecords.Add(new UsageRecord
        {
            MessageId = messageId,
            UserId = userId,
            Provider = provider,
            Model = model,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            TotalTokens = totalTokens
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<DailyUsageTotal>> GetDailyTotalsAsync(
        string userId, int days, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var cutoff = DateTime.UtcNow.Date.AddDays(-(days - 1));

        // Group/aggregate in SQL, then convert to DateOnly in memory — avoids relying on
        // the provider translating DateOnly.FromDateTime inside the query itself.
        var raw = await db.UsageRecords
            .Where(u => u.UserId == userId && u.CreatedAt >= cutoff)
            .GroupBy(u => u.CreatedAt.Date)
            .Select(g => new { Date = g.Key, Total = g.Sum(x => x.TotalTokens) })
            .ToListAsync(ct);

        return raw
            .Select(r => new DailyUsageTotal(DateOnly.FromDateTime(r.Date), r.Total))
            .OrderBy(d => d.Date)
            .ToList();
    }

    public async Task<List<ProviderModelUsageTotal>> GetProviderModelBreakdownAsync(
        string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.UsageRecords
            .Where(u => u.UserId == userId)
            .GroupBy(u => new { u.Provider, u.Model })
            .Select(g => new ProviderModelUsageTotal(
                g.Key.Provider,
                g.Key.Model,
                g.Sum(x => x.InputTokens),
                g.Sum(x => x.OutputTokens),
                g.Sum(x => x.TotalTokens),
                g.Count()))
            .ToListAsync(ct);
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/HrDashboard.Infrastructure.Tests --filter "FullyQualifiedName~UsageRepositoryTests"`
Expected: PASS (5 tests)

- [ ] **Step 6: Run the full Infrastructure test suite to confirm no regression**

Run: `dotnet test tests/HrDashboard.Infrastructure.Tests`
Expected: PASS (all tests, including the existing `ConversationRepositoryTests`)

- [ ] **Step 7: Commit**

```bash
git add src/HrDashboard.Agents/IUsageRepository.cs src/HrDashboard.Infrastructure/Repositories/UsageRepository.cs tests/HrDashboard.Infrastructure.Tests/UsageRepositoryTests.cs
git commit -m "feat: add IUsageRepository for writing and reading usage records"
```

---

### Task 5: Wire usage capture into `ChatSessionService.SendAsync`

**Files:**
- Modify: `src/HrDashboard.Web/Services/ChatSessionService.cs`
- Modify: `src/HrDashboard.Web/Program.cs` (register `IUsageRepository`)

**Interfaces:**
- Consumes: `IHrAgentService.LastTurnUsage` (Task 2). `IUsageRepository.AddUsageRecordAsync` (Task 4). `MessageDisplay` (existing, returned by `IConversationRepository.AddMessageAsync` — already has an `Id` property, just not currently captured).
- Produces: no new public surface — `SendAsync`'s behavior gains a side effect (writing a `UsageRecord` when usage was reported).

This task has no dedicated xUnit coverage (no `HrDashboard.Web` test project — see Global Constraints); its "test" is the build check plus Task 7's live verification.

- [ ] **Step 1: Add the `IUsageRepository` dependency and capture the saved message**

In `src/HrDashboard.Web/Services/ChatSessionService.cs`, change the primary constructor from:

```csharp
public class ChatSessionService(
    IHrAgentService agent,
    IConversationRepository repo,
    IJSRuntime js,
    ILogger<ChatSessionService> logger)
```

to:

```csharp
public class ChatSessionService(
    IHrAgentService agent,
    IConversationRepository repo,
    IUsageRepository usage,
    IJSRuntime js,
    ILogger<ChatSessionService> logger)
```

Then, in `SendAsync`, replace:

```csharp
            // Persist assistant message with MetricsJson
            var persistStopwatch = Stopwatch.StartNew();
            var metricsJson = metrics.Count > 0
                ? JsonSerializer.Serialize(metrics)
                : null;
            await repo.AddMessageAsync(
                conversationId, MessageRole.Assistant, assistantVm.Content, metricsJson, ct);
            logger.LogInformation("SendAsync: persist assistant message took {ElapsedMs}ms", persistStopwatch.ElapsedMilliseconds);
```

with:

```csharp
            // Persist assistant message with MetricsJson
            var persistStopwatch = Stopwatch.StartNew();
            var metricsJson = metrics.Count > 0
                ? JsonSerializer.Serialize(metrics)
                : null;
            var savedMessage = await repo.AddMessageAsync(
                conversationId, MessageRole.Assistant, assistantVm.Content, metricsJson, ct);
            logger.LogInformation("SendAsync: persist assistant message took {ElapsedMs}ms", persistStopwatch.ElapsedMilliseconds);

            var turnUsage = agent.LastTurnUsage;
            if (turnUsage is not null)
            {
                await usage.AddUsageRecordAsync(
                    savedMessage.Id, userId, turnUsage.Provider, turnUsage.Model,
                    turnUsage.InputTokens, turnUsage.OutputTokens, turnUsage.TotalTokens, ct);
            }
```

- [ ] **Step 2: Register `IUsageRepository` in DI**

In `src/HrDashboard.Web/Program.cs`, right after the existing line `builder.Services.AddScoped<IConversationRepository, ConversationRepository>();`, add:

```csharp
    builder.Services.AddScoped<IUsageRepository, UsageRepository>();
```

- [ ] **Step 3: Build**

```bash
dotnet build HrDashboard.slnx
```

Expected: Build succeeded, 0 error(s).

- [ ] **Step 4: Commit**

```bash
git add src/HrDashboard.Web/Services/ChatSessionService.cs src/HrDashboard.Web/Program.cs
git commit -m "feat: write a UsageRecord after each chat turn that reports usage"
```

---

### Task 6: Usage dashboard dialog

**Files:**
- Create: `src/HrDashboard.Web/Components/Chat/UsageDashboard.razor`
- Modify: `src/HrDashboard.Web/Components/Layout/MainLayout.razor`

**Interfaces:**
- Consumes: `IUsageRepository.GetDailyTotalsAsync`/`GetProviderModelBreakdownAsync` (Task 4). `DailyUsageTotal`, `ProviderModelUsageTotal` (Task 4).
- Produces: `UsageDashboard` component (no parameters), shown via `DialogService.ShowAsync<UsageDashboard>(...)`.

This task has no dedicated xUnit coverage (no `HrDashboard.Web` test project); its "test" is the live verification in Task 7.

- [ ] **Step 1: Create `UsageDashboard.razor`**

Create `src/HrDashboard.Web/Components/Chat/UsageDashboard.razor`:

```razor
@using HrDashboard.Agents
@inject IUsageRepository UsageRepo
@inject AuthenticationStateProvider AuthStateProvider

@if (_loading)
{
    <MudProgressCircular Indeterminate="true" />
}
else if (_dailyTotals.Count == 0 && _breakdown.Count == 0)
{
    <MudText Typo="Typo.body1">No usage recorded yet — ask a few questions in the chat and check back here.</MudText>
}
else
{
    <MudText Typo="Typo.subtitle1" Class="mb-2">Tokens per day (last 30 days)</MudText>
    <MudChart ChartType="ChartType.Bar"
              ChartSeries="@_chartSeries"
              XAxisLabels="@_chartLabels"
              Width="100%"
              Height="260px"
              Class="mb-6" />

    <MudText Typo="Typo.subtitle1" Class="mb-2">By provider / model</MudText>
    <MudDataGrid T="ProviderModelUsageTotal"
                 Items="_breakdown"
                 Dense="true"
                 Striped="true"
                 Hover="true"
                 SortMode="SortMode.Single">
        <Columns>
            <PropertyColumn Property="r => r.Provider" Title="Provider" Sortable="true" />
            <PropertyColumn Property="r => r.Model" Title="Model" Sortable="true" />
            <PropertyColumn Property="r => r.InputTokens" Title="Input Tokens" Sortable="true" Format="N0" />
            <PropertyColumn Property="r => r.OutputTokens" Title="Output Tokens" Sortable="true" Format="N0" />
            <PropertyColumn Property="r => r.TotalTokens" Title="Total Tokens" Sortable="true" Format="N0" />
            <PropertyColumn Property="r => r.TurnCount" Title="Turns" Sortable="true" />
        </Columns>
    </MudDataGrid>
}

@code {
    private bool _loading = true;
    private List<DailyUsageTotal> _dailyTotals = [];
    private List<ProviderModelUsageTotal> _breakdown = [];
    private List<ChartSeries> _chartSeries = [];
    private string[] _chartLabels = [];

    protected override async Task OnInitializedAsync()
    {
        var auth = await AuthStateProvider.GetAuthenticationStateAsync();
        var userId = auth.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (userId is null)
        {
            _loading = false;
            return;
        }

        _dailyTotals = await UsageRepo.GetDailyTotalsAsync(userId, days: 30);
        _breakdown   = await UsageRepo.GetProviderModelBreakdownAsync(userId);

        _chartLabels = _dailyTotals.Select(d => d.Date.ToString("MM/dd")).ToArray();
        _chartSeries =
        [
            new ChartSeries
            {
                Name = "Tokens",
                Data = _dailyTotals.Select(d => (double)d.TotalTokens).ToArray()
            }
        ];

        _loading = false;
    }
}
```

- [ ] **Step 2: Add the sidebar button and dialog trigger**

In `src/HrDashboard.Web/Components/Layout/MainLayout.razor`, in the left panel's header `div` (the one containing the "Toggle sidebar" and "New conversation" `MudIconButton`s), add a third button right after them:

```razor
            <MudIconButton Icon="@Icons.Material.Filled.Insights"
                           Size="Size.Small"
                           OnClick="OpenUsageDashboard"
                           title="Usage" />
```

Then add this method to the `@code` block, alongside the existing `OpenResultsFullScreen`:

```csharp
    private async Task OpenUsageDashboard()
    {
        var options = new DialogOptions { FullScreen = true, CloseButton = true };
        await DialogService.ShowAsync<UsageDashboard>("Usage", options);
    }
```

- [ ] **Step 3: Build**

```bash
dotnet build HrDashboard.slnx
```

Expected: Build succeeded, 0 error(s).

- [ ] **Step 4: Commit**

```bash
git add src/HrDashboard.Web/Components/Chat/UsageDashboard.razor src/HrDashboard.Web/Components/Layout/MainLayout.razor
git commit -m "feat: add usage dashboard dialog with daily chart and provider/model breakdown"
```

---

### Task 7: End-to-end verification and wrap-up

**Files:** none (verification only, plus PR).

- [ ] **Step 1: Run the full test suite**

```bash
dotnet test HrDashboard.slnx --filter "Category!=Integration"
```

Expected: all `HrDashboard.Agents.Tests`, `HrDashboard.Infrastructure.Tests`, and `HrDashboard.McpServer.Tests` pass. `HrDashboard.Web.E2E.Tests` may show the pre-existing, already-documented flaky `Login_InvalidCredentials_ShowsError` failure (bug-023 in `.wolf/cerebrum.md`) — this is unrelated to this branch and not a regression to chase.

- [ ] **Step 2: Live verification — start both processes**

```bash
dotnet build HrDashboard.slnx
dotnet run --project src/HrDashboard.McpServer
```

In a second terminal:

```bash
dotnet run --project src/HrDashboard.Web
```

Confirm the startup log shows the migration applying cleanly (no errors) and `AI provider: {Provider} | Model: {Model}` logs as before.

- [ ] **Step 3: Verify a usage record is written for a real turn**

Log in, ask any question (e.g. a preset chip like "Average salary by department"), wait for the response. Then query the database directly to confirm a row exists:

```bash
sqlcmd -S "(localdb)\mssqllocaldb" -d HrDashboard -Q "SELECT TOP 5 Provider, Model, InputTokens, OutputTokens, TotalTokens, CreatedAt FROM UsageRecords ORDER BY CreatedAt DESC" -C
```

Expected: at least one row, with `Provider`/`Model` matching the active `AI:Provider` configuration, and plausible non-zero token counts.

**If the row's token counts are all zero** (rather than the row being entirely absent), that means the active provider's `IChatClient` implementation isn't populating `Usage`/`UsageContent` for that call — note which provider/phase (Phase 1 non-streaming vs. Phase 2 streaming) this affects and record it as a known limitation (see spec Testing section) rather than treating it as a defect to chase further in this task.

- [ ] **Step 4: Verify the dashboard dialog**

In the browser, click the new "Usage" icon button in the sidebar. Confirm:
- The dialog opens full-screen.
- The bar chart renders with at least one day's bar (today's).
- The provider/model breakdown table shows a row matching what Step 3 confirmed in the database, with sortable columns.

- [ ] **Step 5: Repeat against a second provider**

The spec calls for checking usage reporting against each of this project's configured providers, since whether `Usage`/`UsageContent` gets populated is a per-provider-SDK behavior, not something guaranteed by the abstraction. Azure OpenAI is not usable in this dev environment without a real API key in user secrets (see `appsettings.json`'s placeholder), so this step covers the two that are: switch `AI:Provider` to `Ollama` in `src/HrDashboard.Web/appsettings.json` (temporary, local-only — do not commit this change), confirm Ollama is running (`curl http://localhost:11434/api/tags`), restart the web app, and repeat Step 3's database check.

Record what you find for each provider (Nvidia and Ollama) in the PR description's test plan: whether `UsageRecords` rows appear with non-zero `InputTokens`/`OutputTokens`/`TotalTokens`, or whether one provider's rows come through as zero (partial reporting) or don't appear at all (no reporting) for that turn. Any provider that doesn't fully report usage is a documented, accepted limitation per the spec's Decision 2 ("passive log, not a requirement every turn must satisfy") — not a defect to fix in this task.

Revert `appsettings.json` back to `"Provider": "Nvidia"` before moving on — confirm with `git diff src/HrDashboard.Web/appsettings.json` that it shows no changes.

- [ ] **Step 6: Push branch and open PR**

```bash
git push -u origin feature/chat-usage-logging
gh pr create --base develop --title "Chat token/cost usage logging" --body "$(cat <<'EOF'
## Summary
- Logs token usage (input/output/total) per chat turn, tagged by provider/model, persisted per user.
- New usage dashboard (full-screen dialog, matching this app's existing single-page pattern) shows a 30-day daily-tokens chart and a provider/model breakdown table.
- No dollar-cost estimation or active limit enforcement in this pass \u2014 passive observability only.

## Test plan
- [x] `dotnet test HrDashboard.slnx` passes (except the pre-existing, unrelated bug-023 flaky E2E test)
- [x] Live verification: a real chat turn produces a UsageRecord row with plausible token counts; the usage dashboard dialog renders the chart and breakdown table correctly
- [x] Checked against both Nvidia and Ollama — [fill in actual findings here: did both report non-zero usage, or does one under-report?]

Spec: docs/superpowers/specs/2026-08-24-chat-usage-logging-design.md
Plan: docs/superpowers/plans/2026-08-24-chat-usage-logging.md
EOF
)"
```

Do not merge the PR without the user's explicit go-ahead — merging and branch deletion are a separate, confirmed step in this project's workflow.

# Listing-Query Confirm Dialog Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ground the chat agent's response to listing-style requests (e.g. "list employees") in a real column-selection dialog sourced from an actual `DescribeTable` result, instead of the model guessing chart-vs-listing shape and inventing or omitting columns. Also show a zero-token data-availability summary when a chat starts, so a user unfamiliar with the schema knows what's there to ask about.

**Architecture:** `HrAgentService`'s tool-use loop gains a code-side `ToolCallTracker` that classifies each turn by which tools were actually invoked (schema-only vs. data-returning) rather than by parsing the model's prose. When a turn is schema-only with a plain-text response, the real column list captured from the `DescribeTable` result is surfaced through `IHrAgentService` as `PendingColumnOptions`. `ChatSessionService` attaches it to the chat message, and `ChatThread.razor` renders it as multi-select confirm chips. Selecting columns and confirming sends a normal follow-up chat message, reusing the existing pipeline — no new persistence or execution path is needed. Separately, `HrAgentService` gains a `GetSchemaOverviewAsync` method that invokes the `ListTables`/`DescribeTable` tools directly (bypassing the LLM entirely), which `PromptBar.razor` renders as a summary card shown only before the first message is sent.

**Tech Stack:** .NET 10, Blazor Server, Microsoft.Extensions.AI 10.9.0 (`IChatClient`), MudBlazor 8.15.0, xUnit + FluentAssertions + NSubstitute.

**Spec:** docs/superpowers/specs/2026-08-23-hr-chat-confirm-dialog-design.md

## Global Constraints

- GitFlow branching: this work happens on `feature/listing-column-confirm-dialog`, branched off `develop`. If a bug is found and fixed mid-implementation, branch any follow-up fix as `bugfix/<description>`, never `fix/<description>`.
- Do not include `Co-Authored-By: Claude ... <noreply@anthropic.com>` in commit messages (project preference, see `.wolf/cerebrum.md`).
- Offered columns must always come from a real `DescribeTable` tool result captured in code — never parsed out of the model's natural-language text.
- Aggregate/metric requests and the existing preset report chips are unaffected — no confirm step for those.
- Chip selection state is session-only; it is never persisted to the database (spec Design Decision 4).
- This project's `HrDashboard.Web` layer has no xUnit test project — Web-layer changes are verified live via Playwright, matching established project practice (see `.wolf/cerebrum.md` bug-084/089 fixes).

---

### Task 1: `PendingColumnOptions` model + `ToolCallTracker`

**Files:**
- Create: `src/HrDashboard.Agents/Models/PendingColumnOptions.cs`
- Create: `src/HrDashboard.Agents/ToolCallTracker.cs`
- Test: `tests/HrDashboard.Agents.Tests/PendingColumnOptionsTests.cs`
- Test: `tests/HrDashboard.Agents.Tests/ToolCallTrackerTests.cs`

**Interfaces:**
- Produces: `HrDashboard.Agents.Models.PendingColumnOptions(string TableName, IReadOnlyList<string> Columns)` with method `HashSet<string> GetDefaultSelectedColumns()`. `HrDashboard.Agents.ToolCallTracker` (internal, visible to `HrDashboard.Agents.Tests` via the existing `InternalsVisibleTo`) with `void Observe(FunctionCallContent call, object? result)`, `PendingColumnOptions? Classify(string finalText)`, and read-only properties `bool DataToolCalled`, `string? LastDescribeTableName`, `IReadOnlyList<string>? LastDescribeTableColumns`.
- Consumes: `HrMetricParser.LooksLikeGenuineTextAnswer(string)` (already public, in `HrDashboard.Agents.Models`).

- [ ] **Step 1: Create the feature branch**

```bash
git -C c:/apps/HrDashboard checkout develop
git -C c:/apps/HrDashboard pull
git -C c:/apps/HrDashboard checkout -b feature/listing-column-confirm-dialog
```

- [ ] **Step 2: Write the failing tests for `PendingColumnOptions`**

Create `tests/HrDashboard.Agents.Tests/PendingColumnOptionsTests.cs`:

```csharp
using FluentAssertions;
using HrDashboard.Agents.Models;
using Xunit;

namespace HrDashboard.Agents.Tests;

public class PendingColumnOptionsTests
{
    [Fact]
    public void GetDefaultSelectedColumns_ExcludesIdLikeColumns()
    {
        var options = new PendingColumnOptions("EMPLOYEES",
            ["EMPLOYEE_ID", "FIRST_NAME", "SALARY", "DEPARTMENT_ID"]);

        var defaults = options.GetDefaultSelectedColumns();

        defaults.Should().BeEquivalentTo("FIRST_NAME", "SALARY");
    }

    [Fact]
    public void GetDefaultSelectedColumns_AllColumnsAreIdLike_ReturnsEmpty()
    {
        var options = new PendingColumnOptions("LINK_TABLE", ["EMPLOYEE_ID", "DEPARTMENT_ID"]);

        var defaults = options.GetDefaultSelectedColumns();

        defaults.Should().BeEmpty();
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/HrDashboard.Agents.Tests --filter "FullyQualifiedName~PendingColumnOptionsTests"`
Expected: FAIL to build — `PendingColumnOptions` does not exist yet.

- [ ] **Step 4: Create `PendingColumnOptions`**

Create `src/HrDashboard.Agents/Models/PendingColumnOptions.cs`:

```csharp
namespace HrDashboard.Agents.Models;

/// <summary>
/// Real column names captured from a DescribeTable tool result, offered to the user as
/// a clarification for a listing-style request instead of guessing which columns they
/// want. Never constructed from model-authored text — only from an actual tool result,
/// so the offered columns can never be hallucinated.
/// </summary>
public sealed record PendingColumnOptions(string TableName, IReadOnlyList<string> Columns)
{
    /// <summary>
    /// A simple, deterministic default selection: every real column except ones that
    /// look like raw identifier/key columns (name ends in "Id", case-insensitive, which
    /// also covers "_ID" suffixes). Not a perfect heuristic — a column that happens to
    /// end in "id" for another reason would also be excluded — but good enough for a
    /// sensible default the user can freely adjust via the chips.
    /// </summary>
    public HashSet<string> GetDefaultSelectedColumns() =>
        Columns.Where(c => !c.EndsWith("Id", StringComparison.OrdinalIgnoreCase))
               .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/HrDashboard.Agents.Tests --filter "FullyQualifiedName~PendingColumnOptionsTests"`
Expected: PASS (2 tests)

- [ ] **Step 6: Write the failing tests for `ToolCallTracker`**

Create `tests/HrDashboard.Agents.Tests/ToolCallTrackerTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace HrDashboard.Agents.Tests;

public class ToolCallTrackerTests
{
    private const string ValidDescribeTableJson =
        """[{"column_name":"EMPLOYEE_ID","data_type":"NUMBER","is_nullable":"NO"},{"column_name":"FIRST_NAME","data_type":"VARCHAR2","is_nullable":"YES"},{"column_name":"SALARY","data_type":"NUMBER","is_nullable":"YES"}]""";

    [Fact]
    public void Observe_DescribeTableWithValidJson_CapturesColumns()
    {
        var tracker = new ToolCallTracker();
        var call = new FunctionCallContent("call-1", "DescribeTable",
            new Dictionary<string, object?> { ["tableName"] = "EMPLOYEES" });

        tracker.Observe(call, ValidDescribeTableJson);

        tracker.LastDescribeTableName.Should().Be("EMPLOYEES");
        tracker.LastDescribeTableColumns.Should().Equal("EMPLOYEE_ID", "FIRST_NAME", "SALARY");
        tracker.DataToolCalled.Should().BeFalse();
    }

    [Fact]
    public void Observe_DataToolCall_SetsDataToolCalled()
    {
        var tracker = new ToolCallTracker();
        var call = new FunctionCallContent("call-1", "RunHrQuery", null);

        tracker.Observe(call, "[]");

        tracker.DataToolCalled.Should().BeTrue();
    }

    [Fact]
    public void Observe_ListTablesCall_DoesNotSetDataToolCalledOrColumns()
    {
        var tracker = new ToolCallTracker();
        var call = new FunctionCallContent("call-1", "ListTables", null);

        tracker.Observe(call, """["EMPLOYEES","DEPARTMENTS"]""");

        tracker.DataToolCalled.Should().BeFalse();
        tracker.LastDescribeTableColumns.Should().BeNull();
    }

    [Fact]
    public void Observe_DescribeTableWithErrorString_DoesNotCaptureColumns()
    {
        var tracker = new ToolCallTracker();
        var call = new FunctionCallContent("call-1", "DescribeTable",
            new Dictionary<string, object?> { ["tableName"] = "NOPE" });

        tracker.Observe(call, "[Rejected: table not in the allowed HR table list]");

        tracker.LastDescribeTableColumns.Should().BeNull();
    }

    [Fact]
    public void Classify_NoDescribeTableCalled_ReturnsNull()
    {
        var tracker = new ToolCallTracker();

        var result = tracker.Classify("Here's a plain answer.");

        result.Should().BeNull();
    }

    [Fact]
    public void Classify_DataToolCalledEvenAfterDescribeTable_ReturnsNull()
    {
        var tracker = new ToolCallTracker();
        tracker.Observe(new FunctionCallContent("call-1", "DescribeTable",
            new Dictionary<string, object?> { ["tableName"] = "EMPLOYEES" }), ValidDescribeTableJson);
        tracker.Observe(new FunctionCallContent("call-2", "RunHrQuery", null), "[]");

        var result = tracker.Classify("Here are the results.");

        result.Should().BeNull();
    }

    [Fact]
    public void Classify_SchemaOnlyAndPlainTextAnswer_ReturnsPendingColumnOptions()
    {
        var tracker = new ToolCallTracker();
        tracker.Observe(new FunctionCallContent("call-1", "DescribeTable",
            new Dictionary<string, object?> { ["tableName"] = "EMPLOYEES" }), ValidDescribeTableJson);

        var result = tracker.Classify("Which columns would you like to see?");

        result.Should().NotBeNull();
        result!.TableName.Should().Be("EMPLOYEES");
        result.Columns.Should().Equal("EMPLOYEE_ID", "FIRST_NAME", "SALARY");
    }

    [Fact]
    public void Classify_SchemaOnlyButTextContainsJsonArray_ReturnsNull()
    {
        var tracker = new ToolCallTracker();
        tracker.Observe(new FunctionCallContent("call-1", "DescribeTable",
            new Dictionary<string, object?> { ["tableName"] = "EMPLOYEES" }), ValidDescribeTableJson);

        var result = tracker.Classify("""[{"label":"IT","value":1,"category":"x"}] Done.""");

        result.Should().BeNull();
    }
}
```

- [ ] **Step 7: Run the tests to verify they fail**

Run: `dotnet test tests/HrDashboard.Agents.Tests --filter "FullyQualifiedName~ToolCallTrackerTests"`
Expected: FAIL to build — `ToolCallTracker` does not exist yet.

- [ ] **Step 8: Create `ToolCallTracker`**

Create `src/HrDashboard.Agents/ToolCallTracker.cs`:

```csharp
using System.Text.Json;
using HrDashboard.Agents.Models;
using Microsoft.Extensions.AI;

namespace HrDashboard.Agents;

/// <summary>
/// Tracks which tools were invoked during one agent turn, so the turn can be classified
/// as a listing-column clarification (only schema tools called, no data returned) versus
/// a normal final answer — without parsing the model's own prose. Grounds the eventual
/// <see cref="PendingColumnOptions"/> in the real DescribeTable tool result, not in
/// anything the model wrote, so offered columns can never be hallucinated.
/// </summary>
internal sealed class ToolCallTracker
{
    public bool DataToolCalled { get; private set; }
    public string? LastDescribeTableName { get; private set; }
    public IReadOnlyList<string>? LastDescribeTableColumns { get; private set; }

    public void Observe(FunctionCallContent call, object? result)
    {
        var isSchemaTool =
            string.Equals(call.Name, "ListTables", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(call.Name, "DescribeTable", StringComparison.OrdinalIgnoreCase);

        if (!isSchemaTool)
        {
            DataToolCalled = true;
            return;
        }

        if (!string.Equals(call.Name, "DescribeTable", StringComparison.OrdinalIgnoreCase))
            return;

        if (result is not string json || !TryParseColumnNames(json, out var columns))
            return;

        LastDescribeTableName = call.Arguments is not null
            && call.Arguments.TryGetValue("tableName", out var tableName)
            ? tableName?.ToString() ?? ""
            : "";
        LastDescribeTableColumns = columns;
    }

    /// <summary>
    /// Returns the real column list to offer as a clarification, or null if this turn
    /// was a normal final answer (a data tool ran, or no DescribeTable columns were
    /// captured, or the response text isn't plain natural language).
    /// </summary>
    public PendingColumnOptions? Classify(string finalText)
    {
        if (DataToolCalled) return null;
        if (LastDescribeTableColumns is null || LastDescribeTableColumns.Count == 0) return null;
        if (!HrMetricParser.LooksLikeGenuineTextAnswer(finalText)) return null;

        return new PendingColumnOptions(LastDescribeTableName ?? "", LastDescribeTableColumns);
    }

    // DescribeTable's JSON result is a row-per-column array (see DbResultSerializer in
    // HrDashboard.McpServer), e.g. [{"column_name":"EMPLOYEE_ID","data_type":"NUMBER",
    // "is_nullable":"NO"},...] — the case of the "column_name" key can vary by provider,
    // hence PropertyNameCaseInsensitive. Non-JSON error strings like
    // "[Rejected: ...]" or "[SQL error: ...]" fail to parse and correctly yield no
    // columns rather than throwing.
    private static bool TryParseColumnNames(string json, out IReadOnlyList<string> columns)
    {
        columns = [];
        try
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var rows = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(json, opts);
            if (rows is null || rows.Count == 0) return false;

            var names = new List<string>();
            foreach (var row in rows)
            {
                var key = row.Keys.FirstOrDefault(k => string.Equals(k, "column_name", StringComparison.OrdinalIgnoreCase));
                if (key is null) return false;

                var value = row[key].ValueKind == JsonValueKind.String ? row[key].GetString() : null;
                if (!string.IsNullOrWhiteSpace(value)) names.Add(value);
            }

            if (names.Count == 0) return false;
            columns = names;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
```

- [ ] **Step 9: Run the tests to verify they pass**

Run: `dotnet test tests/HrDashboard.Agents.Tests --filter "FullyQualifiedName~ToolCallTrackerTests"`
Expected: PASS (7 tests)

- [ ] **Step 10: Commit**

```bash
git add src/HrDashboard.Agents/Models/PendingColumnOptions.cs src/HrDashboard.Agents/ToolCallTracker.cs tests/HrDashboard.Agents.Tests/PendingColumnOptionsTests.cs tests/HrDashboard.Agents.Tests/ToolCallTrackerTests.cs
git commit -m "feat: add PendingColumnOptions and ToolCallTracker for listing-column clarification"
```

---

### Task 2: Widen `AskAsync` to return `PendingColumns`

**Files:**
- Modify: `src/HrDashboard.Agents/IHrAgentService.cs`
- Modify: `src/HrDashboard.Agents/HrAgentService.cs` (`AskAsync` method)
- Test: `tests/HrDashboard.Agents.Tests/HrAgentServiceTests.cs`

**Interfaces:**
- Consumes: `ToolCallTracker` (Task 1) — `new ToolCallTracker()`, `.Observe(call, result)`, `.Classify(rawText)`.
- Produces: `IHrAgentService.AskAsync` now returns `Task<(string RawText, IReadOnlyList<HrMetricRow> Metrics, PendingColumnOptions? PendingColumns)>`.

- [ ] **Step 1: Update the three existing `AskAsync` tests for the widened tuple**

In `tests/HrDashboard.Agents.Tests/HrAgentServiceTests.cs`, update the three `AskAsync_*` test bodies:

```csharp
    [Fact]
    public async Task AskAsync_NoToolCalls_ReturnsDirectResponse()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(TextResponse("The answer is 42.")));

        var sut = Build(client);
        var (raw, metrics, pending) = await sut.AskAsync("test prompt");

        raw.Should().Be("The answer is 42.");
        metrics.Should().BeEmpty(); // no JSON array in "The answer is 42."
        pending.Should().BeNull();
    }

    [Fact]
    public async Task AskAsync_WithOneToolCallThenText_ReturnsParsedMetrics()
    {
        const string finalText = """[{"label":"IT","value":8000,"category":"AvgSalary"}] Done.""";

        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
              .Returns(
                  Task.FromResult(ToolCallResponse("GetSalaryBreakdown")),
                  Task.FromResult(TextResponse(finalText)));

        var sut = Build(client);
        var (raw, metrics, pending) = await sut.AskAsync("salary breakdown");

        raw.Should().Be(finalText);
        metrics.Should().HaveCount(1);
        metrics[0].Label.Should().Be("IT");
        metrics[0].Value.Should().Be(8000.0);
        pending.Should().BeNull(); // GetSalaryBreakdown is a data tool, not schema-only
    }

    [Fact]
    public async Task AskAsync_HitsIterationLimit_ReturnsLimitMessage()
    {
        var client = Substitute.For<IChatClient>();
        // Always return a tool call — never terminates naturally
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
              .Returns(_ => Task.FromResult(ToolCallResponse("loop")));

        var sut = Build(client);
        var (raw, _, pending) = await sut.AskAsync("infinite loop");

        raw.Should().Contain("iteration limit");
        pending.Should().BeNull();
    }
```

- [ ] **Step 2: Add the new clarification-path test**

Add to the same file, in the "AskAsync tests" region:

```csharp
    [Fact]
    public async Task AskAsync_SchemaOnlyThenPlainTextQuestion_ReturnsPendingColumns()
    {
        const string describeTableJson =
            """[{"column_name":"EMPLOYEE_ID","data_type":"NUMBER","is_nullable":"NO"},{"column_name":"FIRST_NAME","data_type":"VARCHAR2","is_nullable":"YES"}]""";

        var client = Substitute.For<IChatClient>();
        var describeCall = new FunctionCallContent("call-1", "DescribeTable",
            new Dictionary<string, object?> { ["tableName"] = "EMPLOYEES" });

        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
              .Returns(
                  Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, [describeCall])])),
                  Task.FromResult(TextResponse("Which columns would you like to see?")));

        Func<string, string> describeTableFn = tableName => describeTableJson;
        var tools = new List<AITool> { AIFunctionFactory.Create(describeTableFn, "DescribeTable", null, null) };
        var sut = new HrAgentService(client, tools, NullLogger<HrAgentService>.Instance);

        var (raw, metrics, pending) = await sut.AskAsync("list employees");

        raw.Should().Be("Which columns would you like to see?");
        metrics.Should().BeEmpty();
        pending.Should().NotBeNull();
        pending!.TableName.Should().Be("EMPLOYEES");
        pending.Columns.Should().Equal("EMPLOYEE_ID", "FIRST_NAME");
    }
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/HrDashboard.Agents.Tests --filter "FullyQualifiedName~HrAgentServiceTests"`
Expected: FAIL to build — `AskAsync` still returns a 2-tuple.

- [ ] **Step 4: Widen `IHrAgentService.AskAsync`**

In `src/HrDashboard.Agents/IHrAgentService.cs`, replace the file contents:

```csharp
using HrDashboard.Agents.Models;

namespace HrDashboard.Agents;

public interface IHrAgentService
{
    /// <summary>
    /// Submits a natural language HR analytics query and returns structured metric rows.
    /// PendingColumns is populated instead of Metrics when the model asked a listing-
    /// column clarification question rather than returning a final data answer.
    /// </summary>
    Task<(string RawText, IReadOnlyList<HrMetricRow> Metrics, PendingColumnOptions? PendingColumns)> AskAsync(
        string prompt,
        CancellationToken ct = default);

    /// <summary>
    /// Streams the AI response chunk by chunk. Runs the tool-use loop internally
    /// (non-streaming), then yields the final text as it arrives.
    /// history = prior (Role, Content) pairs for context — MetricsJson is never included.
    /// Once the returned stream is fully drained, <see cref="LastPendingColumnOptions"/>
    /// reflects whether this turn ended in a listing-column clarification.
    /// </summary>
    IAsyncEnumerable<string> AskStreamAsync(
        IEnumerable<(MessageRole Role, string Content)> history,
        string prompt,
        CancellationToken ct = default);

    /// <summary>
    /// Set by the most recent <see cref="AskStreamAsync"/> call once its stream is fully
    /// drained. Null for a normal final answer or a purely conversational response;
    /// populated when the model asked a listing-column clarification question instead
    /// of calling a data tool, grounded in a real DescribeTable result.
    /// </summary>
    PendingColumnOptions? LastPendingColumnOptions { get; }
}
```

- [ ] **Step 5: Update `HrAgentService.AskAsync`**

In `src/HrDashboard.Agents/HrAgentService.cs`, replace the `AskAsync` method (originally lines 147-196) with:

```csharp
    public async Task<(string RawText, IReadOnlyList<HrMetricRow> Metrics, PendingColumnOptions? PendingColumns)> AskAsync(
        string prompt,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        _logger.LogInformation("HR agent prompt: {Prompt}", prompt);

        var options = new ChatOptions { Tools = [.. _tools] };
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User,   prompt)
        };
        var tracker = new ToolCallTracker();

        for (int i = 0; i < MaxIterations; i++)
        {
            var response = await _chatClient.GetResponseAsync(messages, options, ct);

            foreach (var msg in response.Messages)
                messages.Add(msg);

            var calls = response.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .ToList();

            if (calls.Count == 0)
            {
                var raw = response.Text ?? string.Empty;
                _logger.LogInformation("Agent completed in {Iterations} iteration(s)", i + 1);
                var metrics = HrMetricParser.Parse(raw);
                return (raw, metrics, tracker.Classify(raw));
            }

            foreach (var call in calls)
            {
                _logger.LogDebug("Tool call: {Tool}({Args})", call.Name,
                    string.Join(", ", (call.Arguments ?? new Dictionary<string, object?>()).Select(kv => $"{kv.Key}={kv.Value}")));

                var result = await InvokeToolAsync(call, ct);
                tracker.Observe(call, result);

                messages.Add(new ChatMessage(ChatRole.Tool,
                    [new FunctionResultContent(call.CallId, result)]));
            }
        }

        _logger.LogWarning("Agent hit iteration limit for prompt: {Prompt}", prompt);
        return ("[Agent reached iteration limit — rephrase your query]", [], null);
    }
```

Note: `IHrAgentService.LastPendingColumnOptions` is declared on the interface now, so `HrAgentService` won't compile until the next step adds a backing property — Task 3 later replaces that stub with the real tracking logic.

- [ ] **Step 6: Add the temporary property stub so the project builds**

At the top of the `HrAgentService` class body in `src/HrDashboard.Agents/HrAgentService.cs` (right after the `private bool _initialized;` field), add:

```csharp
    public PendingColumnOptions? LastPendingColumnOptions { get; private set; }
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test tests/HrDashboard.Agents.Tests --filter "FullyQualifiedName~HrAgentServiceTests"`
Expected: PASS (all `AskAsync_*` tests, including the new one; `AskStreamAsync_*` tests still pass unchanged since that method's signature hasn't changed yet)

- [ ] **Step 8: Commit**

```bash
git add src/HrDashboard.Agents/IHrAgentService.cs src/HrDashboard.Agents/HrAgentService.cs tests/HrDashboard.Agents.Tests/HrAgentServiceTests.cs
git commit -m "feat: widen AskAsync to return PendingColumnOptions"
```

---

### Task 3: Wire `ToolCallTracker` into `AskStreamAsync`

**Files:**
- Modify: `src/HrDashboard.Agents/HrAgentService.cs` (`AskStreamAsync` method + usings)
- Test: `tests/HrDashboard.Agents.Tests/HrAgentServiceTests.cs`

**Interfaces:**
- Consumes: `ToolCallTracker` (Task 1). `LastPendingColumnOptions` property stub (Task 2, Step 6).
- Produces: `HrAgentService.LastPendingColumnOptions` is now meaningfully populated after `AskStreamAsync`'s stream is fully drained. `AskStreamAsync`'s own `IAsyncEnumerable<string>` signature is unchanged.

- [ ] **Step 1: Add the new streaming tests**

Add to `tests/HrDashboard.Agents.Tests/HrAgentServiceTests.cs`, in the "AskStreamAsync tests" region:

```csharp
    [Fact]
    public async Task AskStreamAsync_SchemaOnlyThenPlainTextQuestion_SetsLastPendingColumnOptions()
    {
        const string describeTableJson =
            """[{"column_name":"EMPLOYEE_ID","data_type":"NUMBER","is_nullable":"NO"},{"column_name":"SALARY","data_type":"NUMBER","is_nullable":"YES"}]""";

        var client = Substitute.For<IChatClient>();
        var describeCall = new FunctionCallContent("call-1", "DescribeTable",
            new Dictionary<string, object?> { ["tableName"] = "EMPLOYEES" });

        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
              .Returns(
                  Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, [describeCall])])),
                  Task.FromResult(TextResponse(string.Empty))); // breaks Phase 1 loop into Phase 2

        client.GetStreamingResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
              .Returns(FakeStream("Which columns would you like to see?"));

        Func<string, string> describeTableFn = tableName => describeTableJson;
        var tools = new List<AITool> { AIFunctionFactory.Create(describeTableFn, "DescribeTable", null, null) };
        var sut = new HrAgentService(client, tools, NullLogger<HrAgentService>.Instance);

        var chunks = new List<string>();
        await foreach (var chunk in sut.AskStreamAsync([], "list employees"))
            chunks.Add(chunk);

        sut.LastPendingColumnOptions.Should().NotBeNull();
        sut.LastPendingColumnOptions!.TableName.Should().Be("EMPLOYEES");
        sut.LastPendingColumnOptions.Columns.Should().Equal("EMPLOYEE_ID", "SALARY");
    }

    [Fact]
    public async Task AskStreamAsync_WithDataToolCall_LeavesLastPendingColumnOptionsNull()
    {
        var client = Substitute.For<IChatClient>();

        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
              .Returns(
                  Task.FromResult(ToolCallResponse("GetDeptHeadcount")),
                  Task.FromResult(TextResponse(string.Empty)));

        client.GetStreamingResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
              .Returns(FakeStream("""[{"label":"IT","value":5,"category":"Headcount"}] Done."""));

        var sut = Build(client);

        await foreach (var _ in sut.AskStreamAsync([], "headcount per department")) { }

        sut.LastPendingColumnOptions.Should().BeNull();
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/HrDashboard.Agents.Tests --filter "FullyQualifiedName~HrAgentServiceTests"`
Expected: FAIL — `LastPendingColumnOptions` stays null in the first new test (tracker isn't wired into `AskStreamAsync` yet).

- [ ] **Step 3: Wire the tracker into `AskStreamAsync`**

In `src/HrDashboard.Agents/HrAgentService.cs`, add `using System.Text;` to the top of the file (alongside the existing `using System.Diagnostics;`), then replace the `AskStreamAsync` method (originally lines 212-294) with:

```csharp
    public async IAsyncEnumerable<string> AskStreamAsync(
        IEnumerable<(MessageRole Role, string Content)> history,
        string prompt,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        LastPendingColumnOptions = null;
        _logger.LogInformation("HR agent streaming prompt: {Prompt}", prompt);

        var totalStopwatch = Stopwatch.StartNew();
        var toolOptions = new ChatOptions { Tools = [.. _tools] };
        var messages = BuildMessages(history, prompt);
        bool toolsWereUsed = false;
        var tracker = new ToolCallTracker();

        // Phase 1: tool-use loop (non-streaming) to gather Oracle HR data.
        // We do NOT add the final no-tool-call response to messages; Phase 2 streams it.
        for (int i = 0; i < MaxIterations; i++)
        {
            var roundStopwatch = Stopwatch.StartNew();
            var response = await _chatClient.GetResponseAsync(messages, toolOptions, ct);
            _logger.LogInformation("Phase 1 round {Round}: model call took {ElapsedMs}ms", i, roundStopwatch.ElapsedMilliseconds);

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
            if (!string.IsNullOrEmpty(update.Text))
            {
                chunkCount++;
                fullTextBuilder.Append(update.Text);
                yield return update.Text;
            }
        }

        LastPendingColumnOptions = tracker.Classify(fullTextBuilder.ToString());

        _logger.LogInformation(
            "Agent streaming final answer complete: {ChunkCount} chunk(s), phase 2 took {Phase2Ms}ms, {TotalMs}ms total",
            chunkCount, phase2Stopwatch.ElapsedMilliseconds, totalStopwatch.ElapsedMilliseconds);
    }
```

- [ ] **Step 4: Run all Agents tests to verify they pass**

Run: `dotnet test tests/HrDashboard.Agents.Tests`
Expected: PASS (all tests in the project, including every test from Tasks 1-3)

- [ ] **Step 5: Commit**

```bash
git add src/HrDashboard.Agents/HrAgentService.cs tests/HrDashboard.Agents.Tests/HrAgentServiceTests.cs
git commit -m "feat: classify AskStreamAsync turns via ToolCallTracker"
```

---

### Task 4: Rewrite `SystemPrompt` for the listing confirm-first flow

**Files:**
- Modify: `src/HrDashboard.Agents/HrAgentService.cs` (`SystemPrompt` constant only)

**Interfaces:**
- Consumes: nothing new — this is a text-only change to the prompt sent to the LLM.
- Produces: nothing new — behavior change only observable live (system-prompt wording cannot be unit tested, per `.wolf/cerebrum.md`).

- [ ] **Step 1: Replace the `SystemPrompt` constant**

In `src/HrDashboard.Agents/HrAgentService.cs`, replace the `SystemPrompt` constant (originally lines 20-105) with:

```csharp
    private const string SystemPrompt = """
        You are an AI HR Analytics Assistant connected to an HR database via MCP tools.

        Available tools give you access to:
        - Employee salary data by department
        - Department headcounts
        - Job salary ranges
        - Schema discovery (list tables, describe table columns)
        - Custom SQL queries (SELECT only)

        When a user asks an HR analytics question, first decide which of these three
        shapes it is, then follow that shape's rules:

        A. A LISTING of raw records (e.g. "list employees", "show me all managers",
           "who works in Sales") — the user wants individual rows, not a computed number.
        B. A single computed METRIC or aggregate (e.g. "average salary by department",
           "headcount per department", "job salary ranges") — already has a fixed,
           self-explanatory shape: one label/value/category per group.
        C. Not about HR data at all (e.g. stock prices, the weather) — no tool applies.

        ListTables and DescribeTable are schema-discovery tools, free to chain in any
        shape — call them first, as many times as needed, whenever you don't already
        know the exact table or column names you need.

        Shape A — LISTING requests:
        1. Call DescribeTable on the relevant table first, to learn its real column
           names. Never guess, invent, or assume column names.
        2. Then STOP. Do not call RunHrQuery yet. Ask the user, in plain natural
           language, which columns they'd like to see — mention a few of the most
           useful ones as a suggested default, and note that other real columns are
           also available, using only the exact names DescribeTable returned. This
           message is a question, not an answer: do not include a JSON metrics array
           in it.
        3. Once the user replies (in their next message) saying which columns they
           want, call RunHrQuery selecting exactly those columns and report only what
           it actually returned, following the JSON contract below. Set
           "chartable":false and include "labelName"/"valueName"/"categoryName" as
           described below, since a listing is a set of records, not a metric.

        NEVER answer a listing question using only ListTables/DescribeTable results
        without first asking about columns as described above, and NEVER invent,
        guess, or use example/placeholder values (like "John Smith" or "Jane Doe") in
        place of real data.

        Shape B — METRIC/aggregate requests:
        Call exactly ONE tool — the single most specific one whose description matches
        the question (GetTopEarnersByDepartment, GetSalaryBreakdownByDepartment,
        GetDeptHeadcount, GetJobSalaryRanges, or RunHrQuery as a last resort). Each
        tool call costs several seconds of real latency, so calling more than one tool
        for a question a single tool already answers in full is a mistake, not extra
        thoroughness. Once a tool's result answers the question, stop — do not call
        another tool to double-check, cross-reference, or re-derive the same numbers a
        different way. Only call a second tool if the first tool's result is genuinely
        missing something the user asked for. There is nothing to negotiate about
        columns for this shape — report the result directly using the JSON contract
        below.

        Shape C — no matching tool:
        Say so honestly, in plain natural language. Do not call RunHrQuery or any
        other tool against unrelated intent, and do not fabricate an answer.

        If the question is purely conversational and has no HR data to report at all
        (e.g. "who are you", "what can you do", a greeting), also just answer in plain
        natural language — do not invent a row or force a placeholder value just to
        satisfy the JSON format below.

        --- JSON contract for final data answers (Shape A step 3, and Shape B) ---

        Whenever your answer reports actual HR data — a metric, a computed value, or a
        list of records from a tool result — include a JSON array in your final
        response like:
        [{"label":"Executive","value":17000.0,"category":"AvgSalary"},...]

        Each object may also include "chartable":false when the result is a plain
        listing with no meaningful single numeric value per row (e.g. "list
        employees", where each row is a record, not a metric) — omit "chartable" (it
        defaults to true) for genuine metrics like averages, headcounts, or ranges,
        where a bar/line/donut chart makes sense. When "chartable" is false, still set
        "label" to something identifying the row (e.g. an employee's name) and "value"
        to any real numeric field from that row (e.g. salary) rather than a
        placeholder — the data still needs to populate a data table even though no
        chart is drawn from it.

        When "chartable" is false, also include "labelName", "valueName", and (if
        used) "categoryName" giving the real field names those columns hold (e.g.
        "labelName":"Employee Name", "valueName":"Salary", "categoryName":
        "Department") — the results table shows these as its column headers instead
        of the generic "Label"/"Value"/"Category" so a listing reads like real data,
        not abstract metric axes. Repeat the same three names on every row in the
        array. Omit them entirely for genuine metrics (chartable true or absent),
        where "Label"/"Value"/"Category" are already meaningful.

        "labelName"/"valueName"/"categoryName" are additional column-header
        overrides, never a replacement for "label"/"value"/"category" — every row
        must still include real "label" and "value" data (e.g. the actual employee
        name and salary) regardless of whether you also include the header-override
        fields.

        "label" and "category" are always JSON strings, in quotes — even when the
        value looks numeric (e.g. a department ID). Prefer a human-readable name over
        a raw ID when one is available (e.g. the department's name rather than its
        numeric ID).

        "value" is always a JSON number, never null, on any row you do include.

        The JSON array must appear directly in the response text (not in a code
        block). After the JSON, add a one-sentence natural language summary.

        Your final data-answer response must contain ONLY the JSON array followed by
        the one-sentence summary — nothing else. Never repeat, quote, or paraphrase
        the tool call you made or the raw tool result payload; that data is
        scaffolding for you, not something to show the user.

        Never write narration about calling a tool — not in this turn, not in any
        earlier turn. Do not write sentences like "Calling X tool..." or "I'll check
        Y..."; simply invoke the tool directly. Any text you write, in any turn, is
        potentially shown to the user, so it must always be one of: silence (while
        only calling tools), the Shape A column-choice question, a Shape C or
        conversational plain-language answer, or the final JSON array plus
        one-sentence summary — never a description of what you're doing.
        """;
```

- [ ] **Step 2: Run the full Agents test suite to confirm no regression**

Run: `dotnet test tests/HrDashboard.Agents.Tests`
Expected: PASS (all existing tests — none of them assert on `SystemPrompt` content, so a wording-only change shouldn't break any of them)

- [ ] **Step 3: Commit**

```bash
git add src/HrDashboard.Agents/HrAgentService.cs
git commit -m "feat: rewrite SystemPrompt for listing confirm-first flow and off-topic honesty"
```

- [ ] **Step 4: Manual live verification**

This step has no automated test — system-prompt behavior can only be verified against a running model.

1. Start the MCP server: `dotnet run --project src/HrDashboard.McpServer`
2. Start the web app: `dotnet run --project src/HrDashboard.Web`
3. Log in, start a new conversation, ask: `list employees`
4. Confirm the assistant asks a plain-language question about which columns to show (no JSON array, no chart) rather than immediately dumping data.
5. Reply with a column choice (e.g. `just name and salary`) and confirm it now returns real employee rows for exactly those columns.
6. Ask a preset-chip-style aggregate question (e.g. `Average salary by department`) and confirm it still runs straight through with no confirm step, exactly as before.
7. Ask something off-topic (e.g. `what's the weather today`) and confirm it answers honestly that it can't help with that, without attempting a tool call.

If any of these don't hold, note the exact wording the model produced and adjust the `SystemPrompt` text before moving on — do not proceed to Task 5 on a prompt that doesn't pass this check, since Task 5's UI logic depends on `LastPendingColumnOptions` actually getting populated in the live app.

---

### Task 5: Render column-confirm chips in the chat UI

**Files:**
- Modify: `src/HrDashboard.Web/Services/MessageViewModel.cs`
- Modify: `src/HrDashboard.Web/Services/ChatSessionService.cs` (`SendAsync` method)
- Modify: `src/HrDashboard.Web/Components/Chat/ChatThread.razor`

**Interfaces:**
- Consumes: `IHrAgentService.LastPendingColumnOptions` (Task 3). `PendingColumnOptions.GetDefaultSelectedColumns()` (Task 1).
- Produces: `MessageViewModel.PendingColumns` (`PendingColumnOptions?`), `MessageViewModel.SelectedColumns` (`HashSet<string>?`), `MessageViewModel.ColumnsConfirmed` (`bool`) — read by `ChatThread.razor`.

This task has no dedicated xUnit coverage (there is no `HrDashboard.Web` test project — see Global Constraints); its "test" is the live Playwright verification in Step 5.

- [ ] **Step 1: Add the new properties to `MessageViewModel`**

Replace the contents of `src/HrDashboard.Web/Services/MessageViewModel.cs`:

```csharp
using HrDashboard.Agents.Models;

namespace HrDashboard.Web.Services;

public class MessageViewModel
{
    public MessageRole Role { get; init; }
    public string Content { get; set; } = string.Empty;
    public bool IsStreaming { get; set; }
    public IReadOnlyList<HrMetricRow>? Metrics { get; set; }

    /// <summary>Real columns offered for a listing-style clarification. Session-only —
    /// never persisted (see spec Design Decision 4).</summary>
    public PendingColumnOptions? PendingColumns { get; set; }

    /// <summary>The user's current chip toggle state for <see cref="PendingColumns"/>.
    /// Initialized to a sensible default when PendingColumns is set.</summary>
    public HashSet<string>? SelectedColumns { get; set; }

    /// <summary>True once the user has tapped "Show results" — the chip row becomes
    /// display-only after this so scrolling back doesn't offer stale re-submission.</summary>
    public bool ColumnsConfirmed { get; set; }

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

- [ ] **Step 2: Attach `PendingColumns` in `ChatSessionService.SendAsync`**

In `src/HrDashboard.Web/Services/ChatSessionService.cs`, in the `SendAsync` method, right after the line `logger.LogInformation("SendAsync: agent.AskStreamAsync took {ElapsedMs}ms", streamStopwatch.ElapsedMilliseconds);` and before the `var cleaned = HrMetricParser.StripScaffolding(...)` line, insert:

```csharp

            var pendingColumns = agent.LastPendingColumnOptions;
            if (pendingColumns is not null)
            {
                assistantVm.PendingColumns = pendingColumns;
                assistantVm.SelectedColumns = pendingColumns.GetDefaultSelectedColumns();
            }
```

- [ ] **Step 3: Render the chips in `ChatThread.razor`**

In `src/HrDashboard.Web/Components/Chat/ChatThread.razor`, add `@inject AuthenticationStateProvider AuthStateProvider` to the top directive block (alongside the existing `@inject` lines), then replace the assistant-message rendering block:

```razor
        @if (msg.Role == HrDashboard.Agents.Models.MessageRole.User)
        {
            <div class="msg-user">@msg.Content</div>
        }
        else
        {
            <div class="msg-assistant">
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

                @if (msg.PendingColumns is not null)
                {
                    <MudStack Row="true" Wrap="Wrap.Wrap" Spacing="1" Class="mt-2">
                        @foreach (var column in msg.PendingColumns.Columns)
                        {
                            var isSelected = msg.SelectedColumns?.Contains(column) == true;
                            <MudChip T="string"
                                     Color="@(isSelected ? Color.Primary : Color.Default)"
                                     Variant="@(isSelected ? Variant.Filled : Variant.Outlined)"
                                     Size="Size.Small"
                                     Disabled="@(msg.ColumnsConfirmed || Session.IsStreaming)"
                                     OnClick="@(() => ToggleColumn(msg, column))">
                                @column
                            </MudChip>
                        }
                        <MudChip T="string"
                                 Color="Color.Success"
                                 Size="Size.Small"
                                 Disabled="@(msg.ColumnsConfirmed || Session.IsStreaming || msg.SelectedColumns is null || msg.SelectedColumns.Count == 0)"
                                 OnClick="@(() => ConfirmColumnsAsync(msg))">
                            Show results
                        </MudChip>
                    </MudStack>
                }
            </div>
        }
```

Then update the `@code` block to fetch `_userId` and add the two handlers:

```csharp
@code {
    private ElementReference _scrollRef;
    private string? _userId;

    protected override async Task OnInitializedAsync()
    {
        Session.OnChange += OnSessionChange;
        var auth = await AuthStateProvider.GetAuthenticationStateAsync();
        _userId = auth.User.FindFirst(
            System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    }

    private void OnSessionChange()
    {
        InvokeAsync(async () =>
        {
            StateHasChanged();
            await Task.Yield();
        });
    }

    private void ToggleColumn(HrDashboard.Web.Services.MessageViewModel msg, string column)
    {
        if (msg.ColumnsConfirmed || msg.SelectedColumns is null) return;

        if (!msg.SelectedColumns.Remove(column))
            msg.SelectedColumns.Add(column);
    }

    private async Task ConfirmColumnsAsync(HrDashboard.Web.Services.MessageViewModel msg)
    {
        if (msg.ColumnsConfirmed || msg.SelectedColumns is null || msg.SelectedColumns.Count == 0 || _userId is null)
            return;

        msg.ColumnsConfirmed = true;
        var columnList = string.Join(", ", msg.SelectedColumns);
        await Session.SendAsync($"Show columns: {columnList}", _userId);
    }

    // Keeps the transcript pinned to the latest message (including mid-stream chunks)
    // so the user never has to scroll down manually to see new content. Runs after every
    // render, not just OnSessionChange, since streaming chunks trigger renders directly.
    // Best-effort: a JS interop failure (e.g. during prerendering, before the circuit's
    // JS side is attached) must never break the chat UI.
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        try
        {
            await JS.InvokeVoidAsync("hrDashboard.scrollToBottom", _scrollRef);
        }
        catch (JSException)
        {
        }
    }

    public void Dispose() => Session.OnChange -= OnSessionChange;
}
```

(`OnInitialized` becomes `OnInitializedAsync` — replace the old synchronous `OnInitialized` override entirely with the async version above; there should be only one initialization override left in the file.)

- [ ] **Step 4: Build the solution**

Run: `dotnet build HrDashboard.slnx`
Expected: Build succeeds with no errors.

- [ ] **Step 5: Live Playwright verification**

1. Start both processes as in Task 4 Step 4 (skip if already running).
2. Using the Playwright MCP tools, navigate to the app, log in, and ask `list employees`.
3. Confirm the assistant's clarifying question renders, followed by a row of clickable chips showing real column names (e.g. `EMPLOYEE_ID`, `FIRST_NAME`, `SALARY`, ...) plus a "Show results" chip.
4. Confirm a sensible subset is pre-selected (highlighted/filled) and ID-like columns are not.
5. Toggle a couple of chips, click "Show results".
6. Confirm: a new user message appears reading like "Show columns: ...", the assistant then returns a real data table restricted to those columns, and the original chip row is now disabled (no longer clickable).
7. Reload the conversation (navigate away and back, or refresh) and confirm the clarifying question text is still visible (chips do not need to reappear — this is the accepted Design Decision 4 limitation).

- [ ] **Step 6: Commit**

```bash
git add src/HrDashboard.Web/Services/MessageViewModel.cs src/HrDashboard.Web/Services/ChatSessionService.cs src/HrDashboard.Web/Components/Chat/ChatThread.razor
git commit -m "feat: render column-confirm chips for listing-style chat responses"
```

---

### Task 6: Shared schema-JSON parser + `GetSchemaOverviewAsync`

**Files:**
- Create: `src/HrDashboard.Agents/SchemaJsonParser.cs`
- Create: `src/HrDashboard.Agents/Models/TableOverview.cs`
- Modify: `src/HrDashboard.Agents/ToolCallTracker.cs` (delegate its column parsing to the new shared parser)
- Modify: `src/HrDashboard.Agents/IHrAgentService.cs` (add `GetSchemaOverviewAsync`)
- Modify: `src/HrDashboard.Agents/HrAgentService.cs` (implement `GetSchemaOverviewAsync`)
- Test: `tests/HrDashboard.Agents.Tests/SchemaJsonParserTests.cs`
- Test: `tests/HrDashboard.Agents.Tests/HrAgentServiceTests.cs` (new tests)

**Interfaces:**
- Consumes: `_tools` (the `IList<AITool>` field `HrAgentService` already holds from its MCP client), `AIFunction.InvokeAsync`.
- Produces: `HrDashboard.Agents.Models.TableOverview(string TableName, IReadOnlyList<string> Columns)`. `IHrAgentService.GetSchemaOverviewAsync(CancellationToken ct = default) -> Task<IReadOnlyList<TableOverview>>`. `HrDashboard.Agents.SchemaJsonParser.TryParseRowValues(string json, string columnKey, out IReadOnlyList<string> values) -> bool` (internal, reused by `ToolCallTracker`).

- [ ] **Step 1: Write the failing tests for `SchemaJsonParser`**

Create `tests/HrDashboard.Agents.Tests/SchemaJsonParserTests.cs`:

```csharp
using FluentAssertions;
using Xunit;

namespace HrDashboard.Agents.Tests;

public class SchemaJsonParserTests
{
    [Fact]
    public void TryParseRowValues_ValidTableNameRows_ReturnsValues()
    {
        var json = """[{"table_name":"EMPLOYEES"},{"table_name":"DEPARTMENTS"}]""";

        var success = SchemaJsonParser.TryParseRowValues(json, "table_name", out var values);

        success.Should().BeTrue();
        values.Should().Equal("EMPLOYEES", "DEPARTMENTS");
    }

    [Fact]
    public void TryParseRowValues_CaseInsensitiveKey_ReturnsValues()
    {
        var json = """[{"TABLE_NAME":"EMPLOYEES"}]""";

        var success = SchemaJsonParser.TryParseRowValues(json, "table_name", out var values);

        success.Should().BeTrue();
        values.Should().Equal("EMPLOYEES");
    }

    [Fact]
    public void TryParseRowValues_ErrorString_ReturnsFalse()
    {
        var success = SchemaJsonParser.TryParseRowValues("[SQL error: timeout]", "table_name", out var values);

        success.Should().BeFalse();
        values.Should().BeEmpty();
    }

    [Fact]
    public void TryParseRowValues_KeyMissingFromRows_ReturnsFalse()
    {
        var json = """[{"other_key":"x"}]""";

        var success = SchemaJsonParser.TryParseRowValues(json, "table_name", out var values);

        success.Should().BeFalse();
    }

    [Fact]
    public void ExtractStringResult_RawString_ReturnsItUnchanged()
    {
        var result = SchemaJsonParser.ExtractStringResult("hello");

        result.Should().Be("hello");
    }

    [Fact]
    public void ExtractStringResult_JsonElementString_ReturnsUnderlyingString()
    {
        var element = System.Text.Json.JsonDocument.Parse("\"hello\"").RootElement;

        var result = SchemaJsonParser.ExtractStringResult(element);

        result.Should().Be("hello");
    }

    [Fact]
    public void ExtractStringResult_JsonElementNonString_ReturnsNull()
    {
        var element = System.Text.Json.JsonDocument.Parse("42").RootElement;

        var result = SchemaJsonParser.ExtractStringResult(element);

        result.Should().BeNull();
    }

    [Fact]
    public void ExtractStringResult_NullOrOtherType_ReturnsNull()
    {
        SchemaJsonParser.ExtractStringResult(null).Should().BeNull();
        SchemaJsonParser.ExtractStringResult(42).Should().BeNull();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/HrDashboard.Agents.Tests --filter "FullyQualifiedName~SchemaJsonParserTests"`
Expected: FAIL to build — `SchemaJsonParser` does not exist yet.

- [ ] **Step 3: Create `SchemaJsonParser`**

Create `src/HrDashboard.Agents/SchemaJsonParser.cs`:

```csharp
using System.Text.Json;

namespace HrDashboard.Agents;

/// <summary>
/// Shared parsing for the row-per-record JSON that HrDashboard.McpServer's schema tools
/// (ListTables, DescribeTable) return (see DbResultSerializer in that project), e.g.
/// [{"table_name":"EMPLOYEES"},...] or [{"column_name":"EMPLOYEE_ID",...},...]. The key
/// name's case can vary by provider (Oracle vs SQL Server), hence
/// PropertyNameCaseInsensitive. Non-JSON error strings like "[SQL error: ...]" fail to
/// parse and correctly yield no values rather than throwing.
/// </summary>
internal static class SchemaJsonParser
{
    public static bool TryParseRowValues(string json, string columnKey, out IReadOnlyList<string> values)
    {
        values = [];
        try
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var rows = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(json, opts);
            if (rows is null || rows.Count == 0) return false;

            var results = new List<string>();
            foreach (var row in rows)
            {
                var key = row.Keys.FirstOrDefault(k => string.Equals(k, columnKey, StringComparison.OrdinalIgnoreCase));
                if (key is null) return false;

                var value = row[key].ValueKind == JsonValueKind.String ? row[key].GetString() : null;
                if (!string.IsNullOrWhiteSpace(value)) results.Add(value);
            }

            if (results.Count == 0) return false;
            values = results;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // A tool result reaches this codebase two different ways depending on which
    // concrete AIFunction produced it: a real MCP-derived tool's InvokeAsync returns
    // the raw string the server sent, while an AIFunctionFactory.Create-built delegate
    // (used throughout this project's own tests as a fake tool) wraps its return value
    // as a System.Text.Json.JsonElement instead — confirmed by direct inspection against
    // the installed Microsoft.Extensions.AI.Abstractions 10.9.0 package. Every caller
    // that reads a tool's raw JSON text should go through this helper rather than
    // assuming one exact CLR type, the same defensive-parsing posture this project
    // already applies to LLM-sourced HrMetricRow fields (see FlexibleStringConverter).
    public static string? ExtractStringResult(object? result) => result switch
    {
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
        _ => null
    };
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/HrDashboard.Agents.Tests --filter "FullyQualifiedName~SchemaJsonParserTests"`
Expected: PASS (8 tests)

- [ ] **Step 5: Refactor `ToolCallTracker` to delegate to the shared parser**

In `src/HrDashboard.Agents/ToolCallTracker.cs`, remove the `using System.Text.Json;` line (no longer needed once both blocks below delegate to `SchemaJsonParser`), then:

1. Replace the string/JsonElement extraction block inside `Observe`:
   ```csharp
        string? json = result switch
        {
            string s => s,
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } je => je.GetString(),
            _ => null
        };
        if (json is null || !TryParseColumnNames(json, out var columns))
            return;
   ```
   with:
   ```csharp
        var json = SchemaJsonParser.ExtractStringResult(result);
        if (json is null || !TryParseColumnNames(json, out var columns))
            return;
   ```

2. Replace the private `TryParseColumnNames` method body (the whole method, including its leading comment) with:
   ```csharp
    // DescribeTable's JSON result is a row-per-column array (see DbResultSerializer in
    // HrDashboard.McpServer). Delegates to the shared parser also used by
    // GetSchemaOverviewAsync (see SchemaJsonParser.cs).
    private static bool TryParseColumnNames(string json, out IReadOnlyList<string> columns) =>
        SchemaJsonParser.TryParseRowValues(json, "column_name", out columns);
   ```

- [ ] **Step 6: Run the existing `ToolCallTracker` tests to confirm no regression**

Run: `dotnet test tests/HrDashboard.Agents.Tests --filter "FullyQualifiedName~ToolCallTrackerTests"`
Expected: PASS (still 8 tests, unchanged behavior)

- [ ] **Step 7: Write the failing tests for `GetSchemaOverviewAsync`**

Add to `tests/HrDashboard.Agents.Tests/HrAgentServiceTests.cs` (a new region, e.g. after the `AskStreamAsync` tests):

```csharp
    // ── GetSchemaOverviewAsync tests ─────────────────────────────────────────

    [Fact]
    public async Task GetSchemaOverviewAsync_ReturnsTableAndColumnsFromRealToolResults()
    {
        const string listTablesJson = """[{"table_name":"EMPLOYEES"}]""";
        const string describeTableJson =
            """[{"column_name":"EMPLOYEE_ID","data_type":"NUMBER","is_nullable":"NO"},{"column_name":"SALARY","data_type":"NUMBER","is_nullable":"YES"}]""";

        Func<string> listTablesFn = () => listTablesJson;
        Func<string, string> describeTableFn = tableName => describeTableJson;

        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(listTablesFn, "ListTables", null, null),
            AIFunctionFactory.Create(describeTableFn, "DescribeTable", null, null)
        };
        var sut = new HrAgentService(Substitute.For<IChatClient>(), tools, NullLogger<HrAgentService>.Instance);

        var overview = await sut.GetSchemaOverviewAsync();

        overview.Should().HaveCount(1);
        overview[0].TableName.Should().Be("EMPLOYEES");
        overview[0].Columns.Should().Equal("EMPLOYEE_ID", "SALARY");
    }

    [Fact]
    public async Task GetSchemaOverviewAsync_ListTablesToolMissing_ReturnsEmpty()
    {
        var sut = Build(Substitute.For<IChatClient>()); // empty tools list

        var overview = await sut.GetSchemaOverviewAsync();

        overview.Should().BeEmpty();
    }
```

- [ ] **Step 8: Run the tests to verify they fail**

Run: `dotnet test tests/HrDashboard.Agents.Tests --filter "FullyQualifiedName~GetSchemaOverviewAsync"`
Expected: FAIL to build — `GetSchemaOverviewAsync` does not exist yet.

- [ ] **Step 9: Add `TableOverview` and the interface method**

Create `src/HrDashboard.Agents/Models/TableOverview.cs`:

```csharp
namespace HrDashboard.Agents.Models;

/// <summary>
/// One table's real column names, for the data-availability summary shown when a chat
/// starts — built directly from schema-discovery tool results, with no LLM call
/// involved, so it costs zero tokens and can never be hallucinated.
/// </summary>
public sealed record TableOverview(string TableName, IReadOnlyList<string> Columns);
```

In `src/HrDashboard.Agents/IHrAgentService.cs`, add this member to the interface (after `LastPendingColumnOptions`):

```csharp

    /// <summary>
    /// Returns the real table/column schema available to query, built directly from the
    /// ListTables/DescribeTable tool results — no LLM call involved, so this costs zero
    /// tokens and the columns can never be hallucinated. Intended for a chat-start
    /// "what data is available" summary shown alongside the preset chips.
    /// </summary>
    Task<IReadOnlyList<TableOverview>> GetSchemaOverviewAsync(CancellationToken ct = default);
```

- [ ] **Step 10: Implement `GetSchemaOverviewAsync` in `HrAgentService`**

In `src/HrDashboard.Agents/HrAgentService.cs`, add this method (e.g. right after `AskStreamAsync`, before `InvokeToolAsync`):

```csharp
    public async Task<IReadOnlyList<TableOverview>> GetSchemaOverviewAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        var listTablesFn = _tools.OfType<AIFunction>()
            .FirstOrDefault(t => string.Equals(t.Name, "ListTables", StringComparison.OrdinalIgnoreCase));
        if (listTablesFn is null) return [];

        var listResult = await listTablesFn.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>()), ct);
        var listJson = SchemaJsonParser.ExtractStringResult(listResult);
        if (listJson is null || !SchemaJsonParser.TryParseRowValues(listJson, "table_name", out var tableNames))
            return [];

        var describeFn = _tools.OfType<AIFunction>()
            .FirstOrDefault(t => string.Equals(t.Name, "DescribeTable", StringComparison.OrdinalIgnoreCase));
        if (describeFn is null) return [];

        var overviews = new List<TableOverview>();
        foreach (var tableName in tableNames)
        {
            var describeResult = await describeFn.InvokeAsync(
                new AIFunctionArguments(new Dictionary<string, object?> { ["tableName"] = tableName }), ct);
            var describeJson = SchemaJsonParser.ExtractStringResult(describeResult);
            if (describeJson is not null && SchemaJsonParser.TryParseRowValues(describeJson, "column_name", out var columns))
                overviews.Add(new TableOverview(tableName, columns));
        }

        return overviews;
    }
```

- [ ] **Step 11: Run all Agents tests to verify they pass**

Run: `dotnet test tests/HrDashboard.Agents.Tests`
Expected: PASS (all tests in the project)

- [ ] **Step 12: Commit**

```bash
git add src/HrDashboard.Agents/SchemaJsonParser.cs src/HrDashboard.Agents/Models/TableOverview.cs src/HrDashboard.Agents/ToolCallTracker.cs src/HrDashboard.Agents/IHrAgentService.cs src/HrDashboard.Agents/HrAgentService.cs tests/HrDashboard.Agents.Tests/SchemaJsonParserTests.cs tests/HrDashboard.Agents.Tests/HrAgentServiceTests.cs
git commit -m "feat: add GetSchemaOverviewAsync for a zero-token data-availability summary"
```

---

### Task 7: Render the data-availability summary card

**Files:**
- Modify: `src/HrDashboard.Web/Services/ChatSessionService.cs` (cache the schema overview)
- Modify: `src/HrDashboard.Web/Components/Chat/PromptBar.razor` (render the card)

**Interfaces:**
- Consumes: `IHrAgentService.GetSchemaOverviewAsync` (Task 6).
- Produces: `ChatSessionService.SchemaOverview` (`IReadOnlyList<TableOverview>?`), `ChatSessionService.EnsureSchemaOverviewLoadedAsync(CancellationToken ct = default)`.

This task has no dedicated xUnit coverage (no `HrDashboard.Web` test project — see Global Constraints); its "test" is the live Playwright verification in Step 3.

- [ ] **Step 1: Add caching to `ChatSessionService`**

In `src/HrDashboard.Web/Services/ChatSessionService.cs`, add this property (near the other public properties, e.g. after `CurrentMetrics`):

```csharp
    public IReadOnlyList<TableOverview>? SchemaOverview { get; private set; }
```

Add this method (e.g. right before `SendAsync`):

```csharp
    public async Task EnsureSchemaOverviewLoadedAsync(CancellationToken ct = default)
    {
        if (SchemaOverview is not null) return;
        SchemaOverview = await agent.GetSchemaOverviewAsync(ct);
        Notify();
    }
```

- [ ] **Step 2: Render the card in `PromptBar.razor`**

In `src/HrDashboard.Web/Components/Chat/PromptBar.razor`, insert this block right after the opening `<div class="hr-prompt-bar">` line, before the existing `<MudStack Row="true" AlignItems="AlignItems.End" ...>` text-input row:

```razor
    @if (Session.Messages.Count == 0 && Session.SchemaOverview is { Count: > 0 })
    {
        <MudPaper Class="pa-3 mb-3" Outlined="true">
            <MudText Typo="Typo.subtitle2" Class="mb-1">Data available to explore</MudText>
            <MudExpansionPanels Elevation="0">
                @foreach (var table in Session.SchemaOverview)
                {
                    <MudExpansionPanel Text="@table.TableName">
                        <MudText Typo="Typo.body2">@string.Join(", ", table.Columns)</MudText>
                    </MudExpansionPanel>
                }
            </MudExpansionPanels>
        </MudPaper>
    }
```

Then update `OnInitializedAsync` in the `@code` block to also load the overview:

```csharp
    protected override async Task OnInitializedAsync()
    {
        Session.OnChange += StateHasChanged;
        var auth = await AuthStateProvider.GetAuthenticationStateAsync();
        _userId = auth.User.FindFirst(
            System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        await Session.EnsureSchemaOverviewLoadedAsync();
    }
```

- [ ] **Step 3: Build and live-verify**

Run: `dotnet build HrDashboard.slnx` — expect success.

Then, with both processes running (start them per Task 4 Step 4 if not already up), using the Playwright MCP tools:
1. Start a brand new conversation.
2. Confirm the "Data available to explore" card appears above the text input, listing each visible table (e.g. `EMPLOYEES`, `DEPARTMENTS`, `JOBS`, `LOCATIONS`) as an expandable panel; expand one and confirm it lists that table's real columns.
3. Confirm the preset report chips are still there too (card is in addition to them, not instead).
4. Send any message and confirm the card disappears (it's gated on an empty conversation) while the preset chips remain visible.

- [ ] **Step 4: Commit**

```bash
git add src/HrDashboard.Web/Services/ChatSessionService.cs src/HrDashboard.Web/Components/Chat/PromptBar.razor
git commit -m "feat: render data-availability summary card at chat start"
```

---

### Task 8: End-to-end verification and wrap-up

**Files:** none (verification only, plus PR).

- [ ] **Step 1: Run the full test suite**

Run: `dotnet test HrDashboard.slnx`
Expected: PASS for all unit tests (Agents, Infrastructure, McpServer's non-`Category=Integration` tests). The `Category=Integration` tests require Docker Desktop with the `oracle-hr` container healthy (see `.wolf/cerebrum.md`) — run them too if Docker is available: `dotnet test HrDashboard.slnx --filter "Category=Integration"`.

- [ ] **Step 2: Full acceptance pass via Playwright**

Repeat all of Task 4 Step 4, Task 5 Step 5, and Task 7 Step 3's checks in one continuous session, plus:
- Confirm a listing request for a *different* table (e.g. "list departments" if the schema supports it, or "show me all jobs") also goes through the confirm dialog with that table's real columns.
- Confirm asking two listing questions in a row in the same conversation each get their own independent chip row, and only the most recently confirmed one remains interactive-looking as "locked" — both older ones should be locked/disabled.

- [ ] **Step 3: Push branch and open PR**

```bash
git push -u origin feature/listing-column-confirm-dialog
gh pr create --base develop --title "Listing-query confirm dialog" --body "$(cat <<'EOF'
## Summary
- Adds a code-grounded column-selection dialog for listing-style chat requests (e.g. "list employees"), instead of the model guessing chart-vs-listing shape.
- Real columns come from an actual DescribeTable tool result captured by a new ToolCallTracker, never from parsing the model's text.
- Adds a zero-token data-availability summary card at chat start, built directly from ListTables/DescribeTable with no LLM call.
- Aggregate/metric requests and the existing preset report chips are unaffected.

## Test plan
- [x] `dotnet test HrDashboard.slnx` passes
- [x] Live Playwright verification: listing request triggers confirm chips with real columns; confirming returns correct data; aggregate/preset-chip questions run straight through; off-topic questions get an honest non-answer; summary card appears alongside preset chips at chat start and disappears once the conversation starts

Spec: docs/superpowers/specs/2026-08-23-hr-chat-confirm-dialog-design.md
Plan: docs/superpowers/plans/2026-08-23-listing-column-confirm-dialog.md
EOF
)"
```

Do not merge the PR without the user's explicit go-ahead — this repo's workflow treats merging and branch deletion as a separate, confirmed step (see prior sessions' pattern: "approve merge, remove feature branch").

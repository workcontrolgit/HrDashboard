# Automated Testing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add 27 automated tests across three new xUnit projects (McpServer, Agents, Infrastructure) with no external dependencies at test time.

**Architecture:** Three test projects mirror the three testable source projects. Production-code changes are minimal: one new interface (`IOracleBridge`), one internal constructor on `HrAgentService`, and one `InternalsVisibleTo` attribute. All external I/O (Oracle, MCP HTTP, LLM) is replaced by hand-written fakes or NSubstitute stubs.

**Tech Stack:** xUnit 2.x, NSubstitute 5.x (Agents.Tests only), FluentAssertions 6.x, EF Core InMemory 10.x, Microsoft.Extensions.Configuration 10.x

**Spec:** docs/superpowers/specs/2026-08-19-automated-testing-design.md

## Global Constraints

- All test projects target `net10.0`
- No test opens a real network connection, subprocess, or external DB
- `<Nullable>enable</Nullable>` and `<ImplicitUsings>enable</ImplicitUsings>` on all test projects
- `<IsTestProject>true</IsTestProject>` on all test projects
- Each test seeds its own data; no shared state between tests
- FluentAssertions version: `6.*` (Apache-2 licensed)
- NSubstitute version: `5.*`
- xUnit version: `2.*`
- Microsoft.NET.Test.Sdk version: `17.*`

---

### Task 1: IOracleBridge interface + McpServer.Tests (9 tests)

**Files:**
- Create: `src/HrDashboard.McpServer/IOracleBridge.cs`
- Modify: `src/HrDashboard.McpServer/OracleBridge.cs` — add `: IOracleBridge`
- Modify: `src/HrDashboard.McpServer/Tools/HrAnalyticsTools.cs` — constructor param type
- Create: `tests/HrDashboard.McpServer.Tests/HrDashboard.McpServer.Tests.csproj`
- Create: `tests/HrDashboard.McpServer.Tests/FakeOracleBridge.cs`
- Create: `tests/HrDashboard.McpServer.Tests/HrAnalyticsToolsTests.cs`
- Create: `tests/HrDashboard.McpServer.Tests/OracleBridgeTests.cs`
- Modify: `HrDashboard.slnx` — add tests folder + new project

**Interfaces:**
- Produces: `IOracleBridge` with signature `Task<string> RunSqlAsync(string sql, CancellationToken ct = default)`

- [ ] **Step 1: Create IOracleBridge interface**

Create `src/HrDashboard.McpServer/IOracleBridge.cs`:

```csharp
namespace HrDashboard.McpServer;

public interface IOracleBridge
{
    Task<string> RunSqlAsync(string sql, CancellationToken ct = default);
}
```

- [ ] **Step 2: Add IOracleBridge to OracleBridge class declaration**

In `src/HrDashboard.McpServer/OracleBridge.cs`, change line 11:

```csharp
// Before:
public sealed class OracleBridge : IHostedService, IAsyncDisposable

// After:
public sealed class OracleBridge : IHostedService, IAsyncDisposable, IOracleBridge
```

No method changes — `RunSqlAsync` is already `public`.

- [ ] **Step 3: Update HrAnalyticsTools constructor parameter**

In `src/HrDashboard.McpServer/Tools/HrAnalyticsTools.cs`, change line 8:

```csharp
// Before:
public sealed class HrAnalyticsTools(OracleBridge oracle)

// After:
public sealed class HrAnalyticsTools(IOracleBridge oracle)
```

- [ ] **Step 4: Build McpServer — verify 0 errors**

```bash
dotnet build src/HrDashboard.McpServer/HrDashboard.McpServer.csproj
```

Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 5: Create the test project file**

Create `tests/HrDashboard.McpServer.Tests/HrDashboard.McpServer.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.*" />
    <PackageReference Include="FluentAssertions" Version="6.*" />
    <PackageReference Include="Microsoft.Extensions.Configuration" Version="10.*" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Memory" Version="10.*" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\HrDashboard.McpServer\HrDashboard.McpServer.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 6: Add test project to solution**

Open `HrDashboard.slnx` and add a `/tests/` folder with the new project:

```xml
<Solution>
  <Folder Name="/src/">
    <Project Path="src/HrDashboard.Agents/HrDashboard.Agents.csproj" />
    <Project Path="src/HrDashboard.Infrastructure/HrDashboard.Infrastructure.csproj" />
    <Project Path="src/HrDashboard.McpServer/HrDashboard.McpServer.csproj" />
    <Project Path="src/HrDashboard.Web/HrDashboard.Web.csproj" />
  </Folder>
  <Folder Name="/tests/">
    <Project Path="tests/HrDashboard.McpServer.Tests/HrDashboard.McpServer.Tests.csproj" />
  </Folder>
</Solution>
```

- [ ] **Step 7: Create FakeOracleBridge**

Create `tests/HrDashboard.McpServer.Tests/FakeOracleBridge.cs`:

```csharp
using HrDashboard.McpServer;

namespace HrDashboard.McpServer.Tests;

internal sealed class FakeOracleBridge(string returnValue = "[]") : IOracleBridge
{
    public string? LastSql { get; private set; }
    public int CallCount { get; private set; }

    public Task<string> RunSqlAsync(string sql, CancellationToken ct = default)
    {
        LastSql = sql;
        CallCount++;
        return Task.FromResult(returnValue);
    }
}
```

- [ ] **Step 8: Write HrAnalyticsToolsTests — verify each tool calls the bridge and returns the result**

Create `tests/HrDashboard.McpServer.Tests/HrAnalyticsToolsTests.cs`:

```csharp
using FluentAssertions;
using HrDashboard.McpServer.Tools;

namespace HrDashboard.McpServer.Tests;

public class HrAnalyticsToolsTests
{
    [Fact]
    public async Task GetTopEarnersByDepartment_PassesSqlToBridge_ReturnsResult()
    {
        var fake = new FakeOracleBridge("[{\"label\":\"Alice\",\"value\":9000}]");
        var tools = new HrAnalyticsTools(fake);

        var result = await tools.GetTopEarnersByDepartment(topN: 5);

        fake.CallCount.Should().Be(1);
        fake.LastSql.Should().Contain("FETCH FIRST 5 ROWS ONLY");
        result.Should().Be("[{\"label\":\"Alice\",\"value\":9000}]");
    }

    [Fact]
    public async Task GetSalaryBreakdownByDepartment_PassesSqlToBridge_ReturnsResult()
    {
        var fake = new FakeOracleBridge("[{\"label\":\"IT\",\"value\":8000}]");
        var tools = new HrAnalyticsTools(fake);

        var result = await tools.GetSalaryBreakdownByDepartment();

        fake.CallCount.Should().Be(1);
        fake.LastSql.Should().Contain("AVG(e.salary)");
        result.Should().Be("[{\"label\":\"IT\",\"value\":8000}]");
    }

    [Fact]
    public async Task GetDeptHeadcount_PassesSqlToBridge_ReturnsResult()
    {
        var fake = new FakeOracleBridge("[{\"label\":\"HR\",\"value\":10}]");
        var tools = new HrAnalyticsTools(fake);

        var result = await tools.GetDeptHeadcount();

        fake.CallCount.Should().Be(1);
        fake.LastSql.Should().Contain("COUNT(*)");
        result.Should().Be("[{\"label\":\"HR\",\"value\":10}]");
    }

    [Fact]
    public async Task GetJobSalaryRanges_PassesSqlToBridge_ReturnsResult()
    {
        var fake = new FakeOracleBridge("[{\"label\":\"Manager\",\"value\":15000}]");
        var tools = new HrAnalyticsTools(fake);

        var result = await tools.GetJobSalaryRanges();

        fake.CallCount.Should().Be(1);
        fake.LastSql.Should().Contain("max_salary");
        result.Should().Be("[{\"label\":\"Manager\",\"value\":15000}]");
    }

    [Fact]
    public async Task RunHrQuery_SelectStatement_PassesToBridge()
    {
        var fake = new FakeOracleBridge("[{\"label\":\"test\",\"value\":1}]");
        var tools = new HrAnalyticsTools(fake);
        const string sql = "SELECT employee_id FROM employees";

        var result = await tools.RunHrQuery(sql);

        fake.CallCount.Should().Be(1);
        fake.LastSql.Should().Be(sql);
        result.Should().Be("[{\"label\":\"test\",\"value\":1}]");
    }

    [Fact]
    public async Task RunHrQuery_NonSelect_RejectsWithoutCallingBridge()
    {
        var fake = new FakeOracleBridge("should not be returned");
        var tools = new HrAnalyticsTools(fake);

        var result = await tools.RunHrQuery("DELETE FROM employees");

        fake.CallCount.Should().Be(0);
        result.Should().Be("[Rejected: only SELECT statements are permitted]");
    }

    [Fact]
    public async Task RunHrQuery_SelectWithLeadingWhitespace_Accepted()
    {
        var fake = new FakeOracleBridge("[]");
        var tools = new HrAnalyticsTools(fake);

        var result = await tools.RunHrQuery("   SELECT 1 FROM dual");

        fake.CallCount.Should().Be(1);
        result.Should().Be("[]");
    }
}
```

- [ ] **Step 9: Run HrAnalyticsTools tests — verify 7 pass**

```bash
dotnet test tests/HrDashboard.McpServer.Tests --filter "FullyQualifiedName~HrAnalyticsToolsTests" -v normal
```

Expected: `7 passed, 0 failed`

- [ ] **Step 10: Write OracleBridgeTests — verify safe-failure paths**

Create `tests/HrDashboard.McpServer.Tests/OracleBridgeTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace HrDashboard.McpServer.Tests;

public class OracleBridgeTests
{
    private static IConfiguration BuildConfig(string sqlclPath)
    {
        var values = new Dictionary<string, string?>
        {
            ["SqlclMcp:Path"]           = sqlclPath,
            ["SqlclMcp:ConnectionName"] = "hr_local"
        };
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    [Fact]
    public async Task StartAsync_WhenSqlclPathMissing_IsAvailableFalse()
    {
        var bridge = new OracleBridge(BuildConfig(string.Empty));

        await bridge.StartAsync(CancellationToken.None);

        bridge.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task RunSqlAsync_WhenNotStarted_ReturnsUnavailableMessage()
    {
        var bridge = new OracleBridge(BuildConfig(string.Empty));
        // deliberately do NOT call StartAsync

        var result = await bridge.RunSqlAsync("SELECT 1 FROM dual");

        result.Should().Be("[Oracle bridge unavailable — check SqlclMcp:Path configuration]");
    }
}
```

- [ ] **Step 11: Run all McpServer tests — verify 9 pass**

```bash
dotnet test tests/HrDashboard.McpServer.Tests -v normal
```

Expected: `9 passed, 0 failed`

- [ ] **Step 12: Commit**

```bash
git add src/HrDashboard.McpServer/IOracleBridge.cs \
        src/HrDashboard.McpServer/OracleBridge.cs \
        src/HrDashboard.McpServer/Tools/HrAnalyticsTools.cs \
        HrDashboard.slnx \
        tests/HrDashboard.McpServer.Tests/
git commit -m "test: add McpServer.Tests — IOracleBridge interface + 9 tests"
```

---

### Task 2: HrAgentService internal ctor + Agents.Tests (11 tests)

**Files:**
- Modify: `src/HrDashboard.Agents/HrDashboard.Agents.csproj` — add InternalsVisibleTo
- Modify: `src/HrDashboard.Agents/HrAgentService.cs` — add internal constructor
- Create: `tests/HrDashboard.Agents.Tests/HrDashboard.Agents.Tests.csproj`
- Create: `tests/HrDashboard.Agents.Tests/HrMetricParserTests.cs`
- Create: `tests/HrDashboard.Agents.Tests/HrAgentServiceTests.cs`
- Modify: `HrDashboard.slnx` — add new project to tests folder

**Interfaces:**
- Consumes: `HrAgentService` internal ctor `(IChatClient chatClient, IList<AITool> tools, ILogger<HrAgentService> logger)`
- Consumes: `HrMetricParser.Parse(string llmText): IReadOnlyList<HrMetricRow>`

- [ ] **Step 1: Add InternalsVisibleTo to Agents project**

In `src/HrDashboard.Agents/HrDashboard.Agents.csproj`, add inside `<Project>`:

```xml
<ItemGroup>
  <AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleTo">
    <_Parameter1>HrDashboard.Agents.Tests</_Parameter1>
  </AssemblyAttribute>
</ItemGroup>
```

Full file after edit:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.AI" Version="10.9.0" />
    <PackageReference Include="ModelContextProtocol" Version="1.4.1" />
    <PackageReference Include="ModelContextProtocol.Core" Version="1.4.1" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.*" />
  </ItemGroup>
  <ItemGroup>
    <AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleTo">
      <_Parameter1>HrDashboard.Agents.Tests</_Parameter1>
    </AssemblyAttribute>
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Add internal constructor to HrAgentService**

In `src/HrDashboard.Agents/HrAgentService.cs`, add this constructor immediately after the existing public constructor (after line 45):

```csharp
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

- [ ] **Step 3: Build Agents project — verify 0 errors**

```bash
dotnet build src/HrDashboard.Agents/HrDashboard.Agents.csproj
```

Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 4: Create the Agents test project**

Create `tests/HrDashboard.Agents.Tests/HrDashboard.Agents.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.*" />
    <PackageReference Include="FluentAssertions" Version="6.*" />
    <PackageReference Include="NSubstitute" Version="5.*" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.*" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\HrDashboard.Agents\HrDashboard.Agents.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 5: Add Agents test project to solution**

In `HrDashboard.slnx`, add inside the `/tests/` folder:

```xml
<Folder Name="/tests/">
  <Project Path="tests/HrDashboard.McpServer.Tests/HrDashboard.McpServer.Tests.csproj" />
  <Project Path="tests/HrDashboard.Agents.Tests/HrDashboard.Agents.Tests.csproj" />
</Folder>
```

- [ ] **Step 6: Write HrMetricParser tests — pure logic, no mocking**

Create `tests/HrDashboard.Agents.Tests/HrMetricParserTests.cs`:

```csharp
using FluentAssertions;
using HrDashboard.Agents.Models;

namespace HrDashboard.Agents.Tests;

public class HrMetricParserTests
{
    [Fact]
    public void Parse_EmptyString_ReturnsEmpty()
    {
        HrMetricParser.Parse(string.Empty).Should().BeEmpty();
        HrMetricParser.Parse("   ").Should().BeEmpty();
    }

    [Fact]
    public void Parse_NoJsonArray_ReturnsEmpty()
    {
        var result = HrMetricParser.Parse("The average salary is high.");
        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_ValidJsonArray_ReturnsRows()
    {
        const string text = """[{"label":"IT","value":8000.0,"category":"AvgSalary"}]""";

        var result = HrMetricParser.Parse(text);

        result.Should().HaveCount(1);
        result[0].Label.Should().Be("IT");
        result[0].Value.Should().Be(8000.0);
        result[0].Category.Should().Be("AvgSalary");
    }

    [Fact]
    public void Parse_JsonEmbeddedInText_ExtractsRows()
    {
        const string text = """
            Here is the data:
            [{"label":"HR","value":50,"category":"Headcount"}]
            That is all.
            """;

        var result = HrMetricParser.Parse(text);

        result.Should().HaveCount(1);
        result[0].Label.Should().Be("HR");
        result[0].Value.Should().Be(50.0);
        result[0].Category.Should().Be("Headcount");
    }

    [Fact]
    public void Parse_InvalidJson_ReturnsEmptyWithoutThrowing()
    {
        var result = HrMetricParser.Parse("[not valid json{{");
        result.Should().BeEmpty();
    }
}
```

- [ ] **Step 7: Run HrMetricParser tests — verify 5 pass**

```bash
dotnet test tests/HrDashboard.Agents.Tests --filter "FullyQualifiedName~HrMetricParserTests" -v normal
```

Expected: `5 passed, 0 failed`

- [ ] **Step 8: Write HrAgentService tests**

Create `tests/HrDashboard.Agents.Tests/HrAgentServiceTests.cs`:

```csharp
using FluentAssertions;
using HrDashboard.Agents.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace HrDashboard.Agents.Tests;

public class HrAgentServiceTests
{
    // Helper: build a ChatResponse containing plain text (no tool calls)
    private static ChatResponse TextResponse(string text)
    {
        var msg = new ChatMessage(ChatRole.Assistant, text);
        return new ChatResponse([msg]);
    }

    // Helper: build a ChatResponse containing one tool call
    private static ChatResponse ToolCallResponse(string toolName)
    {
        var call = new FunctionCallContent("call-1", toolName, null);
        var msg  = new ChatMessage(ChatRole.Assistant, [call]);
        return new ChatResponse([msg]);
    }

    // Helper: fake IAsyncEnumerable<StreamingChatCompletionUpdate> from chunks
    private static async IAsyncEnumerable<StreamingChatCompletionUpdate> FakeStream(
        params string[] chunks)
    {
        foreach (var chunk in chunks)
        {
            yield return new StreamingChatCompletionUpdate
            {
                Role = ChatRole.Assistant,
                Text = chunk
            };
            await Task.CompletedTask;
        }
    }

    private static HrAgentService Build(IChatClient client)
        => new(client, [], NullLogger<HrAgentService>.Instance);

    // ── AskAsync tests ───────────────────────────────────────────────────────

    [Fact]
    public async Task AskAsync_NoToolCalls_ReturnsDirectResponse()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(TextResponse("The answer is 42.")));

        var sut = Build(client);
        var (raw, metrics) = await sut.AskAsync("test prompt");

        raw.Should().Be("The answer is 42.");
        metrics.Should().BeEmpty(); // no JSON array in "The answer is 42."
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
        var (raw, metrics) = await sut.AskAsync("salary breakdown");

        raw.Should().Be(finalText);
        metrics.Should().HaveCount(1);
        metrics[0].Label.Should().Be("IT");
        metrics[0].Value.Should().Be(8000.0);
    }

    [Fact]
    public async Task AskAsync_HitsIterationLimit_ReturnsLimitMessage()
    {
        var client = Substitute.For<IChatClient>();
        // Always return a tool call — never terminates naturally
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
              .Returns(_ => Task.FromResult(ToolCallResponse("loop")));

        var sut = Build(client);
        var (raw, _) = await sut.AskAsync("infinite loop");

        raw.Should().Contain("iteration limit");
    }

    // ── AskStreamAsync tests ─────────────────────────────────────────────────

    [Fact]
    public async Task AskStreamAsync_NoToolCalls_YieldsDirectChunks()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(TextResponse("Hello world")));

        var sut    = Build(client);
        var chunks = new List<string>();

        await foreach (var chunk in sut.AskStreamAsync([], "hi"))
            chunks.Add(chunk);

        chunks.Should().ContainSingle().Which.Should().Be("Hello world");
        // GetStreamingResponseAsync must NOT have been called (Phase 2 skipped)
        await client.DidNotReceive().GetStreamingResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AskStreamAsync_WithToolCalls_StreamsPhase2()
    {
        var client = Substitute.For<IChatClient>();

        // Phase 1: one tool-call round, then break (no-calls response)
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
              .Returns(
                  Task.FromResult(ToolCallResponse("GetData")),
                  Task.FromResult(TextResponse(string.Empty))); // breaks the loop

        // Phase 2 stream
        client.GetStreamingResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
              .Returns(FakeStream("Hello ", "world"));

        var sut    = Build(client);
        var chunks = new List<string>();

        await foreach (var chunk in sut.AskStreamAsync([], "query"))
            chunks.Add(chunk);

        chunks.Should().Equal("Hello ", "world");
    }

    [Fact]
    public async Task AskStreamAsync_IterationLimit_YieldsLimitChunk()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
              .Returns(_ => Task.FromResult(ToolCallResponse("loop")));

        var sut    = Build(client);
        var chunks = new List<string>();

        await foreach (var chunk in sut.AskStreamAsync([], "loop forever"))
            chunks.Add(chunk);

        chunks.Should().ContainSingle().Which.Should().Contain("iteration limit");
    }
}
```

- [ ] **Step 9: Run all Agents tests — verify 11 pass**

```bash
dotnet test tests/HrDashboard.Agents.Tests -v normal
```

Expected: `11 passed, 0 failed`

If `StreamingChatCompletionUpdate` constructor syntax is wrong, check Microsoft.Extensions.AI source for the correct initializer properties — the `Text` and `Role` properties are set in object-initializer syntax.

- [ ] **Step 10: Commit**

```bash
git add src/HrDashboard.Agents/HrDashboard.Agents.csproj \
        src/HrDashboard.Agents/HrAgentService.cs \
        HrDashboard.slnx \
        tests/HrDashboard.Agents.Tests/
git commit -m "test: add Agents.Tests — internal ctor + HrMetricParser + HrAgentService tests (11 tests)"
```

---

### Task 3: Infrastructure.Tests (7 tests)

**Files:**
- Create: `tests/HrDashboard.Infrastructure.Tests/HrDashboard.Infrastructure.Tests.csproj`
- Create: `tests/HrDashboard.Infrastructure.Tests/TestDbContextFactory.cs`
- Create: `tests/HrDashboard.Infrastructure.Tests/ConversationRepositoryTests.cs`
- Modify: `HrDashboard.slnx` — add new project to tests folder

**Interfaces:**
- Consumes: `ConversationRepository(IDbContextFactory<AppDbContext>)`
- Consumes: `Conversation { UserId, Title }`, `Message { ConversationId, Role, Content, MetricsJson }`

- [ ] **Step 1: Create Infrastructure test project**

Create `tests/HrDashboard.Infrastructure.Tests/HrDashboard.Infrastructure.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.*" />
    <PackageReference Include="FluentAssertions" Version="6.*" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.InMemory" Version="10.*" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\HrDashboard.Infrastructure\HrDashboard.Infrastructure.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Add Infrastructure test project to solution**

In `HrDashboard.slnx`, add inside the `/tests/` folder:

```xml
<Folder Name="/tests/">
  <Project Path="tests/HrDashboard.McpServer.Tests/HrDashboard.McpServer.Tests.csproj" />
  <Project Path="tests/HrDashboard.Agents.Tests/HrDashboard.Agents.Tests.csproj" />
  <Project Path="tests/HrDashboard.Infrastructure.Tests/HrDashboard.Infrastructure.Tests.csproj" />
</Folder>
```

- [ ] **Step 3: Create TestDbContextFactory**

Create `tests/HrDashboard.Infrastructure.Tests/TestDbContextFactory.cs`:

```csharp
using HrDashboard.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HrDashboard.Infrastructure.Tests;

/// <summary>
/// Creates a fresh in-memory AppDbContext per factory instance.
/// Each test creates its own factory → completely isolated DB.
/// </summary>
internal sealed class TestDbContextFactory : IDbContextFactory<AppDbContext>
{
    private readonly DbContextOptions<AppDbContext> _options;

    public TestDbContextFactory()
    {
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    public AppDbContext CreateDbContext() => new(_options);

    public Task<AppDbContext> CreateDbContextAsync(CancellationToken ct = default)
        => Task.FromResult(CreateDbContext());
}
```

- [ ] **Step 4: Write ConversationRepository tests**

Create `tests/HrDashboard.Infrastructure.Tests/ConversationRepositoryTests.cs`:

```csharp
using FluentAssertions;
using HrDashboard.Agents.Models;
using HrDashboard.Infrastructure.Entities;
using HrDashboard.Infrastructure.Repositories;

namespace HrDashboard.Infrastructure.Tests;

public class ConversationRepositoryTests
{
    // ── GetByUserAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetByUserAsync_ReturnsOnlyCallerConversations()
    {
        var factory = new TestDbContextFactory();
        await using (var db = factory.CreateDbContext())
        {
            db.Conversations.Add(new Conversation { UserId = "user-a", Title = "A" });
            db.Conversations.Add(new Conversation { UserId = "user-b", Title = "B" });
            await db.SaveChangesAsync();
        }

        var repo   = new ConversationRepository(factory);
        var result = await repo.GetByUserAsync("user-a");

        result.Should().HaveCount(1);
        result[0].Title.Should().Be("A");
    }

    [Fact]
    public async Task GetByUserAsync_OrderedDescendingByCreatedAt()
    {
        var factory = new TestDbContextFactory();
        await using (var db = factory.CreateDbContext())
        {
            db.Conversations.Add(new Conversation
                { UserId = "u", Title = "Old", CreatedAt = DateTime.UtcNow.AddMinutes(-10) });
            db.Conversations.Add(new Conversation
                { UserId = "u", Title = "New", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        var repo   = new ConversationRepository(factory);
        var result = await repo.GetByUserAsync("u");

        result[0].Title.Should().Be("New");
        result[1].Title.Should().Be("Old");
    }

    // ── CreateAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_PersistsAndReturnsNonEmptyId()
    {
        var factory = new TestDbContextFactory();
        var repo    = new ConversationRepository(factory);

        var summary = await repo.CreateAsync("user-x", "My Conversation");

        summary.Id.Should().NotBeEmpty();
        summary.Title.Should().Be("My Conversation");

        // verify it actually persisted
        var list = await repo.GetByUserAsync("user-x");
        list.Should().HaveCount(1);
    }

    // ── UpdateTitleAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateTitleAsync_ChangesTitle()
    {
        var factory = new TestDbContextFactory();
        var repo    = new ConversationRepository(factory);
        var summary = await repo.CreateAsync("user-y", "Old Title");

        await repo.UpdateTitleAsync(summary.Id, "New Title");

        var list = await repo.GetByUserAsync("user-y");
        list[0].Title.Should().Be("New Title");
    }

    // ── AddMessageAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task AddMessageAsync_PersistsMessageWithRole()
    {
        var factory = new TestDbContextFactory();
        var repo    = new ConversationRepository(factory);
        var conv    = await repo.CreateAsync("user-z", "Conv");

        var msg = await repo.AddMessageAsync(
            conv.Id, MessageRole.User, "Hello!", metricsJson: null);

        msg.Id.Should().NotBeEmpty();
        msg.Role.Should().Be(MessageRole.User);
        msg.Content.Should().Be("Hello!");
        msg.MetricsJson.Should().BeNull();
    }

    // ── GetMessagesForAgentAsync ─────────────────────────────────────────────

    [Fact]
    public async Task GetMessagesForAgentAsync_ReturnsInChronologicalOrder()
    {
        var factory = new TestDbContextFactory();
        var repo    = new ConversationRepository(factory);
        var conv    = await repo.CreateAsync("user-a", "Chat");

        await repo.AddMessageAsync(conv.Id, MessageRole.User,      "First");
        await repo.AddMessageAsync(conv.Id, MessageRole.Assistant, "Second");

        var messages = await repo.GetMessagesForAgentAsync(conv.Id);

        messages.Should().HaveCount(2);
        messages[0].Role.Should().Be(MessageRole.User);
        messages[0].Content.Should().Be("First");
        messages[1].Role.Should().Be(MessageRole.Assistant);
        messages[1].Content.Should().Be("Second");
    }

    // ── GetMessagesForDisplayAsync ───────────────────────────────────────────

    [Fact]
    public async Task GetMessagesForDisplayAsync_ReturnsInChronologicalOrder()
    {
        var factory = new TestDbContextFactory();
        var repo    = new ConversationRepository(factory);
        var conv    = await repo.CreateAsync("user-b", "Display Chat");

        await repo.AddMessageAsync(conv.Id, MessageRole.User,      "Q", metricsJson: null);
        await repo.AddMessageAsync(conv.Id, MessageRole.Assistant, "A", metricsJson: "[{\"label\":\"x\",\"value\":1}]");

        var messages = await repo.GetMessagesForDisplayAsync(conv.Id);

        messages.Should().HaveCount(2);
        messages[0].Content.Should().Be("Q");
        messages[0].MetricsJson.Should().BeNull();
        messages[1].Content.Should().Be("A");
        messages[1].MetricsJson.Should().Contain("label");
    }
}
```

- [ ] **Step 5: Run Infrastructure tests — verify 7 pass**

```bash
dotnet test tests/HrDashboard.Infrastructure.Tests -v normal
```

Expected: `7 passed, 0 failed`

- [ ] **Step 6: Run full solution tests — verify all 27 pass**

```bash
dotnet test
```

Expected: `27 passed, 0 failed` across all three test projects.

- [ ] **Step 7: Commit**

```bash
git add HrDashboard.slnx \
        tests/HrDashboard.Infrastructure.Tests/
git commit -m "test: add Infrastructure.Tests — ConversationRepository 7 tests; all 27 tests passing"
```

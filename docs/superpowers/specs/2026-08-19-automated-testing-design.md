# HrDashboard — Automated Testing Design

**Date:** 2026-08-19  
**Status:** Approved

## Goal

Add a comprehensive automated test suite covering all three testable layers of the solution: Agents, Infrastructure, and McpServer. No external services (Oracle, MCP endpoint, LLM) are required to run any test.

## Scope

Three new test projects under `tests/`. Three small production-code changes to enable testability.

## Production Code Changes

### 1. IOracleBridge interface (McpServer)

New file `src/HrDashboard.McpServer/IOracleBridge.cs`:

```csharp
namespace HrDashboard.McpServer;

public interface IOracleBridge
{
    Task<string> RunSqlAsync(string sql, CancellationToken ct = default);
}
```

`OracleBridge` adds `: IOracleBridge` to its class declaration. No body changes.  
`HrAnalyticsTools` constructor changes from `OracleBridge oracle` to `IOracleBridge oracle`.

### 2. HrAgentService internal constructor (Agents)

New `internal` constructor added to `HrAgentService` that accepts pre-built tools and marks `_initialized = true`. Enables unit tests to bypass the MCP HTTP connection. Exposed to the test project via `InternalsVisibleTo` in the `.csproj`.

## Test Projects

| Project | Layer | Tests | Key dependency |
|---|---|---|---|
| `HrDashboard.McpServer.Tests` | McpServer | 9 | `FakeOracleBridge` (hand-written) |
| `HrDashboard.Agents.Tests` | Agents | 11 | NSubstitute (IChatClient) |
| `HrDashboard.Infrastructure.Tests` | Infrastructure | 7 | EF Core InMemory |

## Tech Stack

- **xUnit 2.\*** — test runner
- **NSubstitute 5.\*** — mock generation (Agents.Tests only)
- **FluentAssertions 6.\*** — readable assertions (all projects)
- **Microsoft.EntityFrameworkCore.InMemory 10.\*** — in-process DB (Infrastructure.Tests only)
- **Microsoft.Extensions.Configuration 10.\*** — IConfiguration for OracleBridge tests (McpServer.Tests)

## Global Constraints

- Target framework: `net10.0` for all test projects
- No test may open a real network connection, file system subprocess, or external DB
- All test projects use `<IsTestProject>true</IsTestProject>` and `<Nullable>enable</Nullable>`
- Tests are independent — each test seeds its own data; no shared state between tests

## McpServer.Tests — Test Cases

| Test | Behavior |
|---|---|
| `GetTopEarnersByDepartment_PassesSqlToBridge_ReturnsResult` | Bridge called; return value passed through |
| `GetSalaryBreakdownByDepartment_PassesSqlToBridge_ReturnsResult` | Same pattern |
| `GetDeptHeadcount_PassesSqlToBridge_ReturnsResult` | Same pattern |
| `GetJobSalaryRanges_PassesSqlToBridge_ReturnsResult` | Same pattern |
| `RunHrQuery_SelectStatement_PassesToBridge` | SELECT statement flows through |
| `RunHrQuery_NonSelect_RejectsWithoutCallingBridge` | `[Rejected...]` returned, bridge never called |
| `RunHrQuery_SelectWithLeadingWhitespace_Accepted` | `"  SELECT..."` passes TrimStart guard |
| `OracleBridge_PathMissing_IsAvailableFalse` | Missing SQLcl path → StartAsync returns without error, IsAvailable false |
| `OracleBridge_NotStarted_RunSqlAsync_ReturnsUnavailableMessage` | RunSqlAsync before StartAsync returns `[Oracle bridge unavailable...]` |

## Agents.Tests — Test Cases

| Test | Behavior |
|---|---|
| `Parse_EmptyString_ReturnsEmpty` | Whitespace/empty → `[]` |
| `Parse_NoJsonArray_ReturnsEmpty` | Plain text → `[]` |
| `Parse_ValidJsonArray_ReturnsRows` | Embedded JSON array → typed rows |
| `Parse_JsonEmbeddedInText_ExtractsRows` | `"text [...] text"` — extracts the array |
| `Parse_InvalidJson_ReturnsEmpty` | Malformed JSON → `[]` (never throws) |
| `AskAsync_NoToolCalls_ReturnsDirectResponse` | ChatClient returns text on first call; (raw, metrics) returned |
| `AskAsync_WithOneToolCall_ReturnsParsedMetrics` | Round 1 = tool call; Round 2 = JSON text; metrics parsed |
| `AskAsync_HitsIterationLimit_ReturnsLimitMessage` | MaxIterations tool-call rounds → returns limit string |
| `AskStreamAsync_NoToolCalls_YieldsDirectChunks` | No tools used → yields Phase 1 text directly, Phase 2 not called |
| `AskStreamAsync_WithToolCalls_StreamsPhase2` | Phase 1 runs tool round, Phase 2 streams final answer |
| `AskStreamAsync_IterationLimit_YieldsLimitChunk` | Loop hits limit → yields limit string |

## Infrastructure.Tests — Test Cases

| Test | Behavior |
|---|---|
| `GetByUserAsync_ReturnsOnlyCallerConversations` | User A's data doesn't bleed into User B |
| `GetByUserAsync_OrderedDescendingByCreatedAt` | Most-recent conversation first |
| `CreateAsync_PersistsAndReturnsNonEmptyId` | Round-trips with correct Title and non-empty Guid |
| `UpdateTitleAsync_ChangesTitle` | Existing row reflects new title after update |
| `AddMessageAsync_PersistsMessageWithRole` | Message saved with correct Role, Content, MetricsJson |
| `GetMessagesForAgentAsync_ReturnsInChronologicalOrder` | Ordered by CreatedAt asc |
| `GetMessagesForDisplayAsync_ReturnsInChronologicalOrder` | Same order, MetricsJson included |

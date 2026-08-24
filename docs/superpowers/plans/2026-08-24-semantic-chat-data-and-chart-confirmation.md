# Semantic Chat Data and Chart Confirmation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the confusing fixed `Label`/`Value`/`Category` presentation with semantic arbitrary-column datasets in chat, and require user confirmation before a chart appears in the results panel.

**Architecture:** Add a JSON-backed `HrDataSet` model containing named columns, row values, and an optional chart recommendation. The agent emits this payload for new responses; a compatibility adapter reads existing `HrMetricRow` persistence. Chat renders the summary and grid, while `ChatSessionService` owns a validated confirmed-chart projection consumed by `ResultsPanel`.

**Tech Stack:** .NET 10, C#, `System.Text.Json`, Blazor Server, MudBlazor 8.15.0, xUnit.

---

### Task 1: Add semantic dataset models and parser

**Files:**
- Create: `src/HrDashboard.Agents/Models/HrDataSet.cs`
- Modify: `src/HrDashboard.Agents/Models/HrMetricRow.cs`
- Test: `tests/HrDashboard.Agents.Tests/HrDataSetParserTests.cs`

- [ ] Write tests for arbitrary columns, many-column rows, numeric values, chart recommendations, malformed recommendations being ignored, empty datasets, and conversion of legacy `HrMetricRow` lists.
- [ ] Implement `HrDataColumn`, `HrDataSetRow`, `HrChartRecommendation`, and `HrDataSet` records with lenient scalar JSON conversion and `ToLegacyMetrics`/`FromLegacyMetrics` compatibility helpers.
- [ ] Add parser methods that find the semantic dataset payload without exposing JSON in display text and return a separate natural-language summary.
- [ ] Run `dotnet test .\tests\HrDashboard.Agents.Tests --filter FullyQualifiedName~HrDataSetParserTests` and confirm all new tests pass.

### Task 2: Update the agent output contract

**Files:**
- Modify: `src/HrDashboard.Agents/IHrAgentService.cs`
- Modify: `src/HrDashboard.Agents/HrAgentService.cs`
- Modify: `tests/HrDashboard.Agents.Tests/HrAgentServiceTests.cs`

- [ ] Add the semantic dataset to the agent result and expose it after `AskStreamAsync` completes without breaking `LastPendingColumnOptions` or usage tracking.
- [ ] Rewrite the final-response prompt so actual data responses contain a `dataset` JSON object with real column names and an optional chart recommendation, followed by one summary sentence; remove the instruction that forces all data through three fixed fields.
- [ ] Preserve plain conversational responses and listing-column clarification responses without fabricated datasets.
- [ ] Add tests for a headcount response producing `Department` and `Count`, a many-column listing, and a response with no chart recommendation.
- [ ] Run the focused agent tests and then `dotnet test .\tests\HrDashboard.Agents.Tests`.

### Task 3: Carry and persist semantic data in chat messages

**Files:**
- Modify: `src/HrDashboard.Web/Services/MessageViewModel.cs`
- Modify: `src/HrDashboard.Web/Services/ChatSessionService.cs`
- Modify: `src/HrDashboard.Infrastructure/Repositories/ConversationRepository.cs`
- Test: `tests/HrDashboard.Infrastructure.Tests/ConversationRepositoryTests.cs`

- [ ] Add dataset and chart-recommendation state to `MessageViewModel`.
- [ ] Parse and persist the semantic dataset separately from human-facing assistant content; keep legacy `MetricsJson` loading through the compatibility adapter.
- [ ] Clear the confirmed chart when a new dataset arrives, but do not clear it for plain text or failed turns.
- [ ] Add repository/service coverage for new dataset JSON and legacy rows.
- [ ] Run the focused infrastructure tests and the agent tests.

### Task 4: Render semantic grids in chat

**Files:**
- Modify: `src/HrDashboard.Web/Components/Chat/ChatThread.razor`
- Create: `src/HrDashboard.Web/Components/Chat/DataSetGrid.razor`
- Modify: `src/HrDashboard.Web/wwwroot/app.css`

- [ ] Render real semantic column headers and row values in the assistant message below its summary.
- [ ] Keep wide/many-column datasets usable with horizontal scrolling and a compact expandable data region.
- [ ] Render chart recommendation text with its proposed X/Y columns and a `Show chart` confirmation action only when the recommendation validates.
- [ ] Ensure legacy metrics continue to render with meaningful headers.
- [ ] Run `dotnet build .\src\HrDashboard.Web\HrDashboard.Web.csproj`.

### Task 5: Make the results panel chart-only and confirmation-driven

**Files:**
- Modify: `src/HrDashboard.Web/Services/ChatSessionService.cs`
- Modify: `src/HrDashboard.Web/Components/Chat/ResultsPanel.razor`
- Test: `tests/HrDashboard.Web.E2E.Tests/AuthTests.cs` or a focused web test file

- [ ] Add a confirmed-chart state containing dataset rows and validated X/Y columns.
- [ ] Implement a confirmation method that rejects missing columns and nonnumeric Y values without changing panel state.
- [ ] Remove the automatic generic data grid from `ResultsPanel`; render only a confirmed chart and explicit axis metadata.
- [ ] Build chart labels and values from selected semantic columns, including MudBlazor’s separate `ChartSeries` and `InputData` bindings where applicable.
- [ ] Add a browser-level assertion that data appears in chat, the results panel is empty before confirmation, and the chart appears after confirmation.
- [ ] Run the focused web build and E2E test.

### Task 6: Regression verification and documentation

**Files:**
- Modify: `README.md` if the local behavior or test commands need documentation.
- Test: all existing test projects.

- [ ] Run `dotnet test .\HrDashboard.slnx --filter "Category!=E2E&Category!=Integration"`.
- [ ] Run the E2E suite where its prerequisites are available.
- [ ] Verify a headcount query, a multi-column listing, a non-chartable result, an empty result, and a legacy conversation.
- [ ] Record the final behavior in `.wolf/memory.md` and any discovered defects in `.wolf/buglog.json`.

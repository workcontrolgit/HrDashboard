# HrDashboard — Chat Token/Cost Usage Logging Design

## Context

This is sub-project 2 of a two-part brainstorm from 2026-08-23 (sub-project 1, the
listing-column confirm dialog, has shipped — see
`docs/superpowers/specs/2026-08-23-hr-chat-confirm-dialog-design.md`). The original
request: track token usage of chat completions over time to support cost control,
given the app has already experimented with multiple `IChatClient` providers (NVIDIA
free tier, Ollama local, Azure OpenAI) with very different cost profiles.

Today there is no visibility into how many tokens a conversation actually consumes,
or how that varies by provider. `HrAgentService.AskStreamAsync` makes multiple real
LLM calls per user turn (each Phase 1 tool-round, plus a Phase 2 streaming final
answer), none of which have their token usage recorded anywhere.

## Goals

- Record token usage (input/output/total) for every chat turn, tagged with the
  provider and model that produced it.
- Persist usage durably (survives app restarts), scoped per user — matching how
  `Conversation`/`Message` are already scoped today.
- A dashboard page showing usage trends over time (daily token totals) and a
  breakdown by provider/model, so a user can see which provider/usage pattern costs
  the most in practice.
- The persistence design supports an admin cross-user view being added later without
  a schema change (every record already carries `UserId`) — but that admin view
  itself is explicitly out of scope for this pass (see Non-Goals).

## Non-Goals

- No dollar-cost estimation. Providers have wildly different (and in two of three
  cases, no real) per-token pricing; a price table would need constant upkeep for
  accuracy it can't reliably deliver. Token counts alone already answer the practical
  question this feature exists for: which provider/pattern uses the most tokens.
- No active enforcement — no budgets, limits, warnings, or blocking. This is passive
  observability only. Enforcement is a meaningfully larger feature (per-user vs.
  global limits, what happens at the limit, overrides) and wasn't asked for.
- No admin/cross-user view in this pass — see Goals for why the schema supports it
  later without rework, but building the admin role/permission system now would be
  speculative; nothing else in this app has an admin concept yet.
- No date-range picker, filtering, or export on the dashboard for v1 — a single fixed
  recent window (last 30 days) plus a provider/model breakdown is the whole surface.
- No per-call (tool-round-level) granularity — see Architecture Decision 1.

## Architecture

### Decision 1 — One usage record per user turn, not per internal LLM call

`AskStreamAsync` makes several real LLM calls per user-visible turn (Phase 1's
tool-loop rounds, each a `GetResponseAsync` call, plus Phase 2's streaming final
answer). Rather than logging each internal call separately, `AskStreamAsync`
accumulates `Microsoft.Extensions.AI.UsageDetails` (`InputTokenCount`,
`OutputTokenCount`, `TotalTokenCount`) across every internal call within one turn —
summing each Phase 1 round's `response.Usage`, plus Phase 2's usage (surfaced as a
`UsageContent` item among the streamed `ChatResponseUpdate.Contents`) — into a single
running total for that turn.

This total is exposed as a new `IHrAgentService.LastTurnUsage` property, set to
`null` at the top of `AskStreamAsync` and populated once the stream fully drains —
the exact same "stateful property read once the stream completes" contract already
established for `LastPendingColumnOptions` in the confirm-dialog feature.
`ChatSessionService.SendAsync` reads it the same way it already reads that.

Provider/model identification does not rely on `IChatClient.GetService<ChatClientMetadata>()`
(uncertain whether all three provider SDKs — OllamaSharp, and the `OpenAI.OpenAIClient`
wrapper used for both NVIDIA and Azure OpenAI — populate this consistently).
`HrAgentService` instead takes the provider/model label as a constructor parameter:
`Program.cs` already resolves and logs this exact string today
(`"AI provider: {Provider} | Model: {Model}"`), so this reuses known-good information
already available at DI-registration time instead of an unverified API surface.

### Decision 2 — Persistence: a `UsageRecord` entity tied to the assistant `Message`

New entity in `HrDashboard.Infrastructure.Entities`:

```csharp
public sealed class UsageRecord
{
    public Guid Id { get; set; }
    public Guid MessageId { get; set; }       // FK -> Message (the assistant message this turn produced)
    public Message Message { get; set; } = null!;
    public string UserId { get; set; } = "";  // denormalized from Conversation.UserId, indexed
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long TotalTokens { get; set; }
    public DateTime CreatedAt { get; set; }
}
```

Tying it to the assistant `Message` (rather than only loosely to the `Conversation`)
makes each turn's usage traceable to the exact exchange that produced it, and leaves
room to show "this answer cost N tokens" inline in the transcript later with no
further schema work. `UserId` is denormalized onto the record (rather than requiring
a join through `Message` → `Conversation` → `UserId` for every dashboard query) since
every dashboard query filters by user first.

A new `IUsageRepository` (separate from `IConversationRepository` — single
responsibility, and usage querying has a different shape than conversation CRUD):
`AddUsageRecordAsync(...)` to write, plus the read methods the dashboard needs
(Decision 3). `ChatSessionService.SendAsync` already calls
`repo.AddMessageAsync(...)` to persist the assistant message, which already returns
a `MessageDisplay` (including the generated `Id`) — `SendAsync` currently discards
that return value; it just needs to capture it (`var savedMessage = await
repo.AddMessageAsync(...)`) to get the `MessageId` for the usage record. No
`IConversationRepository` signature change is needed. `SendAsync` then calls
`AddUsageRecordAsync` right after, reading `agent.LastTurnUsage` — if it's `null`
(the agent call failed before producing a usable response, or the provider didn't
report usage), no record is written for that turn; this is a passive log, not a
requirement every turn must satisfy.

New EF Core migration adds the `UsageRecords` table, following the exact pattern of
the existing `InitialCreate` migration.

### Decision 3 — Dashboard page

A new page (e.g. `/usage`), reachable from the existing sidebar navigation, scoped to
the signed-in user (matching how `Conversation`/`Message` already scope by
`UserId`) — see Decision 4 for why this doesn't block a future admin view. Two views
on one page:

- A bar chart of total tokens per day over the last 30 days, using the same
  `MudChart` component already used in `ResultsPanel.razor` — one visual answer to
  "has my usage been trending up or down."
- A breakdown table grouped by provider + model: total input tokens, output tokens,
  total tokens, and turn count per group — the view that actually answers "which
  provider/model costs the most tokens for how I use it."

No date-range picker, additional filters, or export in this pass — a fixed 30-day
window plus the provider/model breakdown is the complete v1 surface.

### Decision 4 — Per-user scoping now, admin view later without rework

Every `UsageRecord` carries `UserId`, so every dashboard query in this pass filters
`WHERE UserId = <signed-in user>`. An admin cross-user view later needs only a
different query (drop that filter) plus a role/permission check on that page — no
schema change. Building that admin view now is out of scope (see Non-Goals): nothing
else in this app has an admin/role concept yet, and adding one speculatively for a
feature nobody has asked to use yet would be scope creep beyond what this pass needs.

## Components Touched

| File | Change |
|---|---|
| `src/HrDashboard.Agents/HrAgentService.cs` | New constructor parameter for provider/model label. `AskStreamAsync` accumulates `UsageDetails` across all internal LLM calls in a turn. New `LastTurnUsage` property. |
| `src/HrDashboard.Agents/IHrAgentService.cs` | New `LastTurnUsage` property; new `TurnUsageInfo` (or similarly named) model. |
| `src/HrDashboard.Agents/Models/` | New `TurnUsageInfo(string Provider, string Model, long InputTokens, long OutputTokens, long TotalTokens)` record. |
| `src/HrDashboard.Web/Program.cs` | Pass the already-resolved provider/model label string into `HrAgentService`'s constructor. |
| `src/HrDashboard.Infrastructure/Entities/UsageRecord.cs` | New entity (Decision 2). |
| `src/HrDashboard.Infrastructure/AppDbContext.cs` | New `DbSet<UsageRecord>`; `OnModelCreating` configuration (indexes on `UserId`, FK to `Message`). |
| `src/HrDashboard.Infrastructure/Migrations/` | New migration adding the `UsageRecords` table. |
| `src/HrDashboard.Infrastructure/IUsageRepository.cs` + implementation | New repository: `AddUsageRecordAsync`, plus read methods for the dashboard (daily totals, provider/model breakdown), both scoped by `UserId`. |
| `src/HrDashboard.Web/Services/ChatSessionService.cs` | `SendAsync` captures the `MessageDisplay` already returned by `repo.AddMessageAsync(...)` (currently discarded) for its `Id`, reads `agent.LastTurnUsage`, and writes a `UsageRecord` via `IUsageRepository` when non-null. |
| `src/HrDashboard.Web/Components/Pages/` | New `Usage.razor` (or similar) dashboard page: chart + breakdown table. |
| `src/HrDashboard.Web/Components/Layout/` | Sidebar navigation link to the new page. |

## Testing

- Unit tests for the `UsageDetails` accumulation logic in `AskStreamAsync` (sum
  across multiple Phase 1 rounds + Phase 2's `UsageContent`), using the existing
  fake-`IChatClient`/NSubstitute test harness pattern already established in
  `HrAgentServiceTests.cs`.
- Unit tests for `IUsageRepository`'s read methods (daily totals, provider/model
  breakdown), following the existing `ConversationRepositoryTests.cs` pattern
  (EF Core InMemory provider).
- Live verification: run a real conversation against each of the three configured
  providers and confirm a `UsageRecord` is written with plausible, non-zero token
  counts for each — this is the one thing that can't be verified from mocks alone,
  since it depends on whether each provider SDK actually populates `Usage`/
  `UsageContent` in practice. If a provider does NOT report usage, document that as a
  known limitation (a turn against that provider simply produces no `UsageRecord`,
  per Decision 2's "passive log, not a requirement every turn must satisfy") rather
  than treating it as a defect to work around.
- No automated test for the dashboard page itself — `src/HrDashboard.Web` has no
  xUnit test project (a pre-existing, accepted project characteristic); verify via
  live browser testing instead, consistent with how the confirm-dialog feature's UI
  layer was verified.

## Follow-Up (explicitly out of scope here)

- Dollar-cost estimation, if a reliable price-table maintenance process is ever
  wanted.
- Active budget enforcement (limits, warnings, blocking).
- Admin cross-user dashboard view (schema already supports it — see Decision 4).
- Date-range filtering / export on the dashboard.

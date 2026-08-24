# HrDashboard — Listing-Query Confirm Dialog Design

## Context

Predefined report chips and curated analytics tools (`GetTopEarnersByDepartment`,
`GetSalaryBreakdownByDepartment`, `GetDeptHeadcount`, `GetJobSalaryRanges`) already
produce a fixed, self-explanatory shape — one number per group — so the user never
needs to negotiate what they'll see.

Free-text **listing/raw-record** requests are different: when a user asks "list
employees," the model has no way to know which columns the user actually wants, and
the user has no way to know which columns exist. Today the model guesses, and with a
weaker LLM (`meta/llama-3.1-8b-instruct`, see `.wolf/cerebrum.md` bug-094/099/102/108)
that guess is frequently wrong — chart-shaped output for a plain listing, or a listing
missing the field the user actually cared about (e.g. no name column).

This design adds a **clarify-before-run dialog** for listing-style requests: the agent
asks the user which columns they want, grounded in the real schema, before running the
query — instead of guessing.

This is sub-project 1 of a two-part brainstorm. **Token/cost usage logging is a
separate, later sub-project** and is out of scope here (see Non-Goals).

## Goals

- For listing/raw-record requests, the agent asks a clarifying question about which
  columns to show, before calling the data-returning tool.
- The columns offered are always real column names sourced from the database schema —
  never invented or hallucinated by the model.
- The user answers via clickable multi-select chips, not free text.
- Aggregate/metric questions and the existing preset report chips are unaffected —
  they keep running straight through with no confirm step.
- Genuinely out-of-scope questions (no matching tool at all) get an honest "I can't
  help with that" instead of a fabricated or misapplied answer.

## Non-Goals

- Token/cost usage logging and any per-conversation cost controls — separate
  sub-project, separate spec.
- Confirming/negotiating aggregate query shape (grouping, which metric) — aggregate
  tools already have a fixed, understood shape.
- Persisting clarification chip state across a page reload — see Design Decision 4.
- Multi-table join column selection — first version handles single-table listing
  requests (the shape `RunHrQuery`/schema tools already handle today).
- Any change to the preset report chips' behavior.

## Architecture / Data Flow

### Design Decision 1 — Turn classification is code-driven, not prose-parsed

`HrAgentService`'s tool-use loop (`AskAsync`/`AskStreamAsync`) already lets the model
freely chain `ListTables`/`DescribeTable` calls before deciding on a data tool. The
system prompt gains an instruction: for listing-style requests, after discovering the
real columns via `DescribeTable`, stop and ask the user which columns they want in
plain natural language — do not call `RunHrQuery` (or any data-returning tool) yet.

Code classifies what happened **based on which tools were actually invoked this
turn**, not by parsing the model's text:

- Only schema tools (`ListTables`/`DescribeTable`) were called, or none, **and** the
  response contains no `HrMetricRow` JSON array (`HrMetricParser.TryParse` returns
  false while `LooksLikeGenuineTextAnswer` is true) → **clarification turn**. The most
  recent `DescribeTable` tool result's column list is captured.
- Any data-returning tool (`RunHrQuery`, `GetTopEarnersByDepartment`,
  `GetSalaryBreakdownByDepartment`, `GetDeptHeadcount`, `GetJobSalaryRanges`) was
  called → **final-answer turn**, unchanged from today's behavior.

This avoids repeating this codebase's recurring failure mode (trusting an LLM to
correctly format structured output — see cerebrum.md bug-094/099/102) for the new
column-list contract: the real column names come from the actual tool result JSON
already flowing through the loop, never from re-parsing the model's sentence.

### Design Decision 2 — Widened agent service contract

`IHrAgentService.AskAsync`/`AskStreamAsync` return types grow to carry an optional
pending-column-options payload alongside the existing text/metrics, e.g.:

```csharp
public sealed record PendingColumnOptions(string TableName, IReadOnlyList<string> Columns);
```

Populated only on a clarification turn; `null`/absent on a normal final-answer turn or
a purely conversational response. `ChatSessionService.SendAsync` reads this to decide
whether to attach column chips to the new `MessageViewModel`.

### Design Decision 3 — Chip UI and the confirm/re-ask cycle

- `MessageViewModel` gains an optional `PendingColumnOptions` property.
- `ChatThread.razor` renders these as multi-select `MudChip`s directly under that
  assistant message, plus a "Show results" action chip.
- **Default selection**: pre-check every column except ones that look like raw
  identifier columns — name is exactly `Id`, or ends in `Id`/`_ID` (case-insensitive).
  This is a deterministic, code-side heuristic; it does not depend on the model
  mentioning column names correctly.
- Tapping "Show results" does **not** re-invoke tool-choice from scratch. It sends a
  normal follow-up user message into the existing pipeline (e.g. "Show columns: Name,
  Department, Salary"), reusing `ChatSessionService.SendAsync` unchanged. The model
  sees its own prior clarifying question plus this reply in conversation history and
  now calls the real data tool with those columns, returning the final `HrMetricRow`
  array exactly as it does today for any other listing result (`chartable:false`,
  `labelName`/`valueName`/`categoryName` populated per the existing contract).
- Once "Show results" is tapped, that message's chip row becomes display-only (locked,
  non-interactive) so scrolling back doesn't offer stale re-submission.

### Design Decision 4 — Chip state is not persisted

Chip selections are session/UI state only — not written to the database. The
underlying clarifying question text is already persisted via the existing
`Message.Content` column, same as any other assistant message. If a user reloads mid-
dialog, the question text is still visible but the chip row will not re-render; the
user can type a plain-text reply instead (the model still understands it from
conversation history). This is an accepted v1 limitation, not a bug — building
persistence for an in-flight, single-exchange UI affordance is out of proportion to
the problem it would solve.

### Design Decision 5 — "No tool available" honesty

Small system-prompt addition: if a request is genuinely unrelated to HR data (e.g.
stock prices, weather) and no available tool matches, the model states this honestly
in plain text rather than attempting `RunHrQuery` against unrelated intent or
fabricating an answer. No chips are involved — there is nothing to choose from. This
reuses the existing "plain text, no JSON array → shown as-is" path
(`LooksLikeGenuineTextAnswer`) with no code changes beyond the prompt wording.

## Components Touched

| File | Change |
|---|---|
| `src/HrDashboard.Agents/HrAgentService.cs` | System prompt: listing-style requests stop after schema discovery and ask about columns instead of calling a data tool immediately; add "no tool available" honesty instruction. Turn-classification logic added to `AskAsync`/`AskStreamAsync`. Return contract widened with `PendingColumnOptions`. |
| `src/HrDashboard.Agents/Models/` | New `PendingColumnOptions` record (or similar). |
| `src/HrDashboard.Agents/IHrAgentService.cs` | Updated method signatures to carry the new optional payload. |
| `src/HrDashboard.Web/Services/ChatSessionService.cs` | `SendAsync` reads the new payload and attaches it to the created `MessageViewModel` instead of (or alongside) normal content handling. |
| `src/HrDashboard.Web/Services/MessageViewModel.cs` | New optional `PendingColumnOptions` property; add a "locked/answered" flag for the display-only-after-selection behavior. |
| `src/HrDashboard.Web/Components/Chat/ChatThread.razor` | Renders multi-select chips + "Show results" action chip under a clarification message; sends the follow-up message on confirm; locks the row after. |

## Testing

- `tests/HrDashboard.Agents.Tests/HrAgentServiceTests.cs`: unit tests for turn
  classification (schema-only tool calls + no metrics array ⇒ clarification payload
  populated; data-tool call ⇒ payload absent), using the existing fake-tool test
  harness pattern.
- Unit tests for the ID-column-exclusion default-selection heuristic (e.g. `EmployeeId`
  excluded, `Name`/`Salary`/`Department` included).
- Live/Playwright verification pass for the actual dialog wording and chip flow —
  system-prompt behavior itself cannot be unit tested directly, consistent with how
  prior prompt changes in this project were verified (see cerebrum.md).

## Follow-Up (separate sub-project)

Token/cost usage logging for chat completions, to support cost control over time. Not
designed here — will get its own brainstorm and spec once this sub-project ships.

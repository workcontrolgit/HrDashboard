# HrDashboard — Claude-Like Layout Redesign

**Date:** 2026-08-19  
**Status:** Approved for implementation planning  
**Author:** Brainstorming session (Fuji Nguyen + Claude)

---

## Overview

Redesign the HrDashboard web UI from a single-page prompt-results layout into a three-panel shell modeled after Claude.ai. The redesign adds persistent chat history (SQL Server), streaming AI responses (token-by-token), and ASP.NET Core Identity authentication.

---

## Goals

- Three-panel layout: left (conversation history), center (chat thread + prompt), right (chart + data grid)
- Collapsible left and right panels via toggle buttons
- Streaming AI responses rendered chunk-by-chunk in the center panel
- Chat history persisted to SQL Server; survives page refresh
- ASP.NET Core Identity for login/register; all routes require auth
- Clean architecture: data/identity in a new `HrDashboard.Infrastructure` project
- Token efficiency: structured metrics (MetricsJson) never sent back to the agent as conversation context

---

## Non-Goals

- External OIDC / Azure AD (future)
- Role-based access control (future)
- Mobile-responsive layout (future)
- Conversation sharing or export (future)

---

## Section 1: Project Structure & Data Layer

### New project: `HrDashboard.Infrastructure`

**References:** `HrDashboard.Agents`, EF Core + SQL Server, ASP.NET Core Identity

**Entities:**

```csharp
class AppUser : IdentityUser { }

class Conversation
{
    Guid Id
    string UserId          // FK to AspNetUsers
    string Title           // First 60 chars of first user prompt
    DateTime CreatedAt
    ICollection<Message> Messages
}

class Message
{
    Guid Id
    Guid ConversationId    // FK to Conversation
    MessageRole Role       // enum: User | Assistant
    string Content         // Plain text — the only thing sent to the agent
    string? MetricsJson    // Serialized List<HrMetricRow> — display only, never sent to agent
    DateTime CreatedAt
}
```

**DbContext:** `AppDbContext : IdentityDbContext<AppUser>`  
Owns `Conversations` and `Messages` DbSets. SQL Server connection string from `appsettings.json`.

**Repository interface** (defined in `HrDashboard.Agents` or a shared contracts assembly):

```csharp
interface IConversationRepository
{
    Task<List<Conversation>> GetByUserAsync(string userId);
    Task<Conversation> CreateAsync(string userId, string title);
    Task<List<(MessageRole Role, string Content)>> GetMessagesForAgentAsync(Guid conversationId);
    Task<List<Message>> GetMessagesForDisplayAsync(Guid conversationId);
    Task<Message> AddMessageAsync(Guid conversationId, MessageRole role, string content, string? metricsJson = null);
    Task UpdateTitleAsync(Guid conversationId, string title);
}
```

**Key design rule:** `GetMessagesForAgentAsync` returns only `(Role, Content)` tuples — never `MetricsJson`. This ensures structured data never consumes agent tokens.

---

## Section 2: Streaming Agent Layer

### Updated `IHrAgentService`

```csharp
interface IHrAgentService
{
    // Existing — kept for any non-streaming callers
    Task<(string Raw, IReadOnlyList<HrMetricRow> Metrics)> AskAsync(string prompt);

    // New — streams text chunks as they arrive from Azure OpenAI
    IAsyncEnumerable<string> AskStreamAsync(
        IEnumerable<(MessageRole Role, string Content)> history,
        string prompt,
        CancellationToken ct = default);
}
```

`AskStreamAsync` accepts the full conversation history (text only) so the agent has context. It yields string chunks as the model streams them using `CompleteChatStreamingAsync` or `GetStreamingChatMessageContentsAsync` from the Azure OpenAI / IChatClient abstraction.

### New: `HrMetricParser`

Static class in `HrDashboard.Agents`:

```csharp
static class HrMetricParser
{
    static List<HrMetricRow> Parse(string fullAgentText);
}
```

Called once after the stream completes. Centralizes metric extraction logic that currently lives inline in `HrAgentService`. The Web layer calls this after accumulating all chunks — no dependency on agent internals.

---

## Section 3: Layout — Three-Panel Shell

### Visual structure

```
┌─────────────┬──────────────────────────┬──────────────────┐
│  Left Panel │     Center Panel         │   Right Panel    │
│  ~260px     │     flex-grow            │   ~360px         │
│             │                          │                  │
│ [≡] [New +] │  ┌─ message thread ────┐ │ [◁] [Chart ▼]  │
│             │  │  User: ...          │ │  <MudChart />   │
│ Conversation│  │  AI: (streaming...) │ │                  │
│ list items  │  │  User: ...          │ │  <MudDataGrid /> │
│             │  └─────────────────────┘ │                  │
│ ─────────── │                          │  (hidden when    │
│ 👤 Username │  [prompt textarea  ] [▶] │   no results)   │
│ [Logout]    │  [preset chips...]       │                  │
└─────────────┴──────────────────────────┴──────────────────┘
```

### Panel toggle behavior

- **Left panel:** Toggle button collapses to icon-only (~56px) or expands to full width (~260px). Default: expanded.
- **Right panel:** Toggle button (tab on left edge of the right drawer) fully hides or shows the panel. When hidden, center panel fills the freed space. Default: open. The panel interior shows chart + grid only when `CurrentMetrics` is non-empty; otherwise it shows a placeholder ("Run a query to see results here"). The toggle is independent of whether metrics exist.
- **State:** `bool _leftOpen`, `bool _rightOpen` on `MainLayout` — in-memory only, no persistence.

### New Razor components

| Component | Location | Responsibility |
|-----------|----------|----------------|
| `ConversationList.razor` | `Layout/` | Left panel: list of past conversations + New Chat button + user profile/logout |
| `ChatThread.razor` | `Components/Chat/` | Center: scrollable message bubbles (user right, AI left) |
| `PromptBar.razor` | `Components/Chat/` | Sticky bottom: textarea + send button + preset chips |
| `ResultsPanel.razor` | `Components/Chat/` | Right panel: chart type selector + MudChart + MudDataGrid |

`Dashboard.razor` hosts all four, wired via `ChatSessionService`.

---

## Section 4: Authentication

### Setup (`Program.cs`)

```csharp
builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlServer(connectionString));

builder.Services.AddIdentity<AppUser, IdentityRole>()
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();

builder.Services.AddAuthentication();
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
```

### New pages (`Components/Pages/Auth/`)

- `Login.razor` — Email + password form. Calls `SignInManager.PasswordSignInAsync`. Redirects to `/` on success.
- `Register.razor` — Email + password + confirm. Calls `UserManager.CreateAsync`. Auto-signs in on success.

### Route protection

`Routes.razor` uses `<AuthorizeRouteView>` with `<NotAuthorized>` redirecting to `/login`. All routes require authentication.

### Logout

Minimal `MapPost("/account/logout")` endpoint in `Program.cs` calls `SignOutAsync` and redirects to `/login`. The logout button in `ConversationList.razor` posts to this endpoint.

### Left panel bottom

```razor
<AuthorizeView>
    <Authorized>
        <MudText>@context.User.Identity?.Name</MudText>
        <MudIconButton ... href="/account/logout" ... />
    </Authorized>
</AuthorizeView>
```

---

## Section 5: Chat Session Service & Streaming State

### `ChatSessionService` (scoped)

```csharp
class ChatSessionService
{
    Conversation? CurrentConversation { get; }
    List<MessageViewModel> Messages { get; }   // MessageViewModel includes IsStreaming flag
    bool IsStreaming { get; }
    List<HrMetricRow> CurrentMetrics { get; }  // drives right panel

    event Action? OnChange;                    // subscribers call StateHasChanged()

    Task StartNewConversationAsync(string userId);
    Task LoadConversationAsync(Guid conversationId);
    Task SendAsync(string prompt, string userId, CancellationToken ct);
}
```

### `SendAsync` flow

1. Append `User` message to `Messages` → save to DB via repository
2. Append empty `Assistant` `MessageViewModel` with `IsStreaming = true` → fire `OnChange`
3. Fetch agent context: `GetMessagesForAgentAsync(conversationId)` (text only, no MetricsJson)
4. `await foreach (var chunk in _agent.AskStreamAsync(history, prompt, ct))`:
   - Append chunk to assistant message content
   - Fire `OnChange` → Blazor re-renders streaming text
5. Stream complete → call `HrMetricParser.Parse(fullContent)` → set `CurrentMetrics`
6. Fire `OnChange` → right panel shows chart/grid
7. Save completed assistant message + serialized MetricsJson to DB
8. Set `IsStreaming = false` → fire `OnChange`

### Auto-title

After step 7, if this is the first exchange in the conversation, set `Title = prompt[..Math.Min(60, prompt.Length)]` and call `UpdateTitleAsync`. No additional LLM call.

### `MessageViewModel`

```csharp
class MessageViewModel
{
    MessageRole Role { get; }
    string Content { get; set; }      // grows chunk by chunk during streaming
    bool IsStreaming { get; set; }
    List<HrMetricRow>? Metrics { get; set; }  // set after stream ends (display only)
}
```

---

## Data Flow Summary

```
User types prompt
    → PromptBar fires SendAsync
    → ChatSessionService appends user message, fetches history (text only)
    → AskStreamAsync yields chunks
    → ChatThread re-renders each chunk (OnChange)
    → Stream ends → HrMetricParser extracts metrics
    → ResultsPanel re-renders chart + grid (OnChange)
    → Full message + MetricsJson saved to DB
    → Auto-title set if first exchange
```

---

## Project Dependency Graph

```
HrDashboard.Web
    → HrDashboard.Infrastructure  (EF, Identity, ConversationRepository)
    → HrDashboard.Agents          (IHrAgentService, HrMetricParser, IConversationRepository)

HrDashboard.Infrastructure
    → HrDashboard.Agents          (IConversationRepository interface)

HrDashboard.McpServer             (standalone, unchanged)
```

---

## Files to Create / Modify

### New project
- `src/HrDashboard.Infrastructure/HrDashboard.Infrastructure.csproj`
- `src/HrDashboard.Infrastructure/AppDbContext.cs`
- `src/HrDashboard.Infrastructure/AppUser.cs`
- `src/HrDashboard.Infrastructure/Entities/Conversation.cs`
- `src/HrDashboard.Infrastructure/Entities/Message.cs`
- `src/HrDashboard.Infrastructure/Repositories/ConversationRepository.cs`
- `src/HrDashboard.Infrastructure/Migrations/` (EF generated)

### Modified: `HrDashboard.Agents`
- `IHrAgentService.cs` — add `AskStreamAsync`
- `HrAgentService.cs` — implement `AskStreamAsync`, extract `HrMetricParser`
- `HrMetricParser.cs` — new static class
- `IConversationRepository.cs` — new interface

### Modified: `HrDashboard.Web`
- `HrDashboard.Web.csproj` — add Infrastructure reference
- `Program.cs` — add Identity, EF, auth, logout endpoint
- `appsettings.json` — add `ConnectionStrings:DefaultConnection`
- `Components/App.razor` — wrap with `<CascadingAuthenticationState>`
- `Components/Routes.razor` — `<AuthorizeRouteView>`
- `Components/Layout/MainLayout.razor` — 3-panel shell with toggles
- `Components/Layout/ConversationList.razor` — new
- `Components/Chat/ChatThread.razor` — new
- `Components/Chat/PromptBar.razor` — new
- `Components/Chat/ResultsPanel.razor` — new
- `Components/Pages/Dashboard.razor` — refactored to host components
- `Components/Pages/Auth/Login.razor` — new
- `Components/Pages/Auth/Register.razor` — new
- `wwwroot/app.css` — panel layout styles

---

## Open Questions / Assumptions

- SQL Server connection string will be configured in `appsettings.Development.json` (not committed); production will use environment variable or Key Vault.
- No email confirmation for registration — simplest viable auth for now.
- `HrMetricParser` logic assumed to be extractable from existing `HrAgentService` with no behavioral changes.
- MudBlazor's `MudDrawer` with `Variant=Persistent` and `ClipMode=Always` used for left panel; right panel uses CSS flex to show/hide.

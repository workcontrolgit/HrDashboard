# HrDashboard — Web E2E Testing Design

**Date:** 2026-08-20
**Status:** Approved

## Goal

Add browser-driven end-to-end tests for `HrDashboard.Web`'s authentication flow (register, login, invalid login, logout), using Playwright's .NET bindings. No Node.js/npm dependency — Playwright's browser binaries are installed via a PowerShell script bundled in the NuGet package.

## Scope

One new test project under `tests/`. One new test-only launch profile is not required — the fixture starts the app directly as a subprocess. No production code changes.

Explicitly out of scope for this round: the chat flow (sending a message, seeing MCP/Oracle/LLM-backed results render). That needs the MCP server, Oracle SQLcl, and an LLM provider all running, which makes it slow and environment-fragile. The design below leaves room to add it later without restructuring.

## Why NUnit (not xUnit)

The rest of the solution's test projects (`HrDashboard.McpServer.Tests`, `HrDashboard.Agents.Tests`, `HrDashboard.Infrastructure.Tests`) use xUnit. This project uses NUnit instead, specifically to use Playwright's official `Microsoft.Playwright.NUnit` package, which provides a `PageTest` base class that handles per-test browser/context/page creation and teardown automatically. xUnit has no equivalent first-party package; using it here would mean hand-writing that lifecycle management. The trade-off (a second test framework in the solution, scoped to this one project) is accepted for that reduction in boilerplate and maintenance surface.

## App Lifecycle

Playwright drives a real browser against a real running server — there is no in-memory `TestServer` option for Blazor Interactive Server, since the UI depends on a live SignalR circuit. The test project therefore launches `HrDashboard.Web` as a subprocess before any test runs, and tears it down after the whole suite completes.

**`[SetUpFixture]` (assembly-level, NUnit):**
1. Starts `dotnet run --project src/HrDashboard.Web --no-launch-profile --urls http://localhost:5100` as a `Process`, with `ASPNETCORE_ENVIRONMENT=Development`, redirecting stdout/stderr for diagnostics on failure.
   - Uses plain HTTP on port 5100, not the HTTPS profile (7100). This sidesteps the ASP.NET Core dev-certificate trust prompt, which Playwright's default browser context does not handle and which would otherwise make every navigation fail with a certificate error.
   - `--no-launch-profile` prevents `launchSettings.json`'s `launchBrowser: true` from opening a real browser window during the automated run.
2. Polls `http://localhost:5100/login` (GET, short timeout per attempt) until it returns a successful response, up to an overall 60-second budget. On timeout, fails the whole suite immediately with a clear message — most likely cause is SQL Server LocalDB not being reachable, since the Web app auto-applies EF migrations and needs the DB at startup.
3. After the suite finishes (`[OneTimeTearDown]`), kills the process **tree** (`Process.Kill(entireProcessTree: true)`) — `dotnet run` launches a child process for the actual app, and killing only the parent leaves the app running and the port bound.

Each test method gets its own isolated `IPage` via `Microsoft.Playwright.NUnit.PageTest`, targeting Chromium only for this pass (no Firefox/WebKit matrix yet — can be added later by parameterizing the base fixture).

## Test Scenarios

The app runs entirely in Blazor `InteractiveServer` render mode (`App.razor` sets `@rendermode="InteractiveServer"` globally), so form fields are wired up over a live SignalR circuit rather than static HTML. Playwright's `fill()`/`click()` auto-wait for elements to be visible and enabled, which is sufficient for the circuit to be connected by the time interaction happens — no extra synchronization is expected to be needed, but if flakiness shows up here it's the first place to look.

Login/Register forms are plain HTML `<form method="post">` submissions to `/account/login-action` and `/account/register-action` (server-side endpoints, not Blazor event handlers) — so the submit itself is a normal page navigation Playwright can wait on directly.

Each test creates its own user through the UI with a unique email — no direct database seeding, no shared fixtures between tests. Password `"Passw0rd!"` (or similar) is used throughout; Identity's password policy in this project is relaxed (`RequiredLength = 6`, no digit/uppercase/symbol requirement — see `src/HrDashboard.Web/Program.cs`), so any 6+ character string works.

| Test | Steps | Assertion |
|---|---|---|
| `Register_NewUser_Succeeds` | Navigate to `/register`; fill email (`e2e-<guid>@test.local`), password, confirm password; submit | Redirected to `/` (registering signs the user in immediately and redirects to the dashboard root) |
| `Login_ValidCredentials_ReachesDashboard` | Register a fresh user; navigate to `/login`; fill same email/password; submit | Redirected to `/` (`Dashboard.razor`, the `[Authorize]`-protected root route) |
| `Login_InvalidCredentials_ShowsError` | Navigate to `/login`; fill a non-existent email and any password; submit | Page shows the "Invalid email or password." `MudAlert` |
| `Logout_SignedInUser_ReturnsToLogin` | Register + log in a fresh user; trigger logout | Redirected back to `/login` |

Known limitation, accepted for this round: LocalDB accumulates a test user per run (no cleanup). Not a correctness problem — each user has a unique email — just noted so it isn't a surprise later.

## Extensibility for a Future Chat-Flow Suite

The `[SetUpFixture]` in this round only starts `HrDashboard.Web`. A later suite covering the chat flow would extend the fixture to also start `HrDashboard.McpServer` (and depend on Oracle/Ollama being available locally), gated behind an NUnit `[Category("ChatFlow")]` so `dotnet test` can still run the fast, dependency-light auth suite on its own via `--filter "Category!=ChatFlow"` (or by running this project directly, since the whole assembly is auth-only today).

## Project Layout

`tests/HrDashboard.Web.E2E.Tests/HrDashboard.Web.E2E.Tests.csproj`, added to `HrDashboard.slnx` under the existing `/tests/` folder alongside the three xUnit projects.

```
tests/HrDashboard.Web.E2E.Tests/
  HrDashboard.Web.E2E.Tests.csproj
  WebAppFixture.cs        # [SetUpFixture]: start/poll/teardown the subprocess
  AuthTests.cs            # the 4 scenarios above
```

## Tech Stack

- **NUnit 4.\*** / **NUnit3TestAdapter** — test runner (this project only)
- **Microsoft.Playwright.NUnit** — browser automation + `PageTest` base class
- **FluentAssertions 6.\*** — kept for assertion-style consistency with the rest of the solution
- Browser binaries installed once via `pwsh bin/Debug/net10.0/playwright.ps1 install chromium` after first build — no npm/Node.js involved

## Global Constraints

- Target framework: `net10.0`
- `<IsTestProject>true</IsTestProject>`, `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`
- Requires SQL Server LocalDB reachable locally (same prerequisite the Web app already has) — no Oracle, MCP server, or LLM provider required for this suite
- Windows only, consistent with the rest of the solution's local-dev assumptions (README already scopes the whole repo to Windows)
- Tests are independent and order-agnostic; no test depends on another test's data beyond what it creates for itself

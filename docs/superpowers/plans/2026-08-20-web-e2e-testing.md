# Web E2E Testing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a Playwright .NET end-to-end test project covering `HrDashboard.Web`'s authentication flow (register, login, invalid login, logout), with zero Node.js/npm dependency and no manual "start the server first" step.

**Architecture:** A single NUnit test project drives a real Chromium browser against a real `HrDashboard.Web` process, which an assembly-level `[SetUpFixture]` starts as a subprocess before any test runs and tears down after. Tests interact purely over HTTP/browser — no compile-time reference to `HrDashboard.Web` — so this project stays a true black-box consumer of the running app.

**Tech Stack:** NUnit 4.x, Microsoft.Playwright.NUnit 1.x, FluentAssertions 6.x, .NET 10

**Spec:** docs/superpowers/specs/2026-08-20-web-e2e-testing-design.md

## Global Constraints

- Target framework: `net10.0`
- `<IsTestProject>true</IsTestProject>`, `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`
- Requires SQL Server LocalDB reachable locally (same prerequisite `HrDashboard.Web` already has) — no Oracle, MCP server, or LLM provider required
- Web app under test is launched on `http://localhost:5100` (plain HTTP, not the HTTPS profile) to avoid ASP.NET Core dev-certificate trust issues with Playwright's default browser context
- Chromium only for this pass — no Firefox/WebKit matrix
- Tests are independent and order-agnostic; each test creates its own uniquely-emailed user through the UI — no direct database seeding, no shared fixtures between tests

---

### Task 1: Project scaffold

**Files:**
- Create: `tests/HrDashboard.Web.E2E.Tests/HrDashboard.Web.E2E.Tests.csproj`
- Modify: `HrDashboard.slnx` — add to `/tests/` folder
- Modify: `README.md` — document how to run this suite

**Interfaces:**
- Produces: an empty, building NUnit test project wired into the solution, with Playwright's Chromium browser installed locally

- [ ] **Step 1: Create the test project file**

Create `tests/HrDashboard.Web.E2E.Tests/HrDashboard.Web.E2E.Tests.csproj`:

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
    <PackageReference Include="NUnit" Version="4.*" />
    <PackageReference Include="NUnit3TestAdapter" Version="4.*" />
    <PackageReference Include="Microsoft.Playwright.NUnit" Version="1.*" />
    <PackageReference Include="FluentAssertions" Version="6.*" />
  </ItemGroup>
</Project>
```

Note: no `ProjectReference` — this project talks to `HrDashboard.Web` purely over HTTP/browser, never through a compiled reference.

- [ ] **Step 2: Add the project to the solution**

In `HrDashboard.slnx`, add a line to the existing `/tests/` folder (alongside the three xUnit projects):

```xml
  <Folder Name="/tests/">
    <Project Path="tests/HrDashboard.McpServer.Tests/HrDashboard.McpServer.Tests.csproj" />
    <Project Path="tests/HrDashboard.Agents.Tests/HrDashboard.Agents.Tests.csproj" />
    <Project Path="tests/HrDashboard.Infrastructure.Tests/HrDashboard.Infrastructure.Tests.csproj" />
    <Project Path="tests/HrDashboard.Web.E2E.Tests/HrDashboard.Web.E2E.Tests.csproj" />
  </Folder>
```

- [ ] **Step 3: Restore and build — verify 0 errors**

```powershell
dotnet build tests/HrDashboard.Web.E2E.Tests/HrDashboard.Web.E2E.Tests.csproj
```

Expected: `Build succeeded. 0 Error(s)` (zero tests is fine — no test files exist yet).

- [ ] **Step 4: Install the Chromium browser binary**

Playwright's .NET package ships a driver that downloads browsers directly — no npm/Node.js involved.

```powershell
pwsh tests/HrDashboard.Web.E2E.Tests/bin/Debug/net10.0/playwright.ps1 install chromium
```

Expected: output ending in something like `Chromium ... downloaded to ...`. This is a one-time local setup step (like installing the .NET SDK itself) — not part of the automated test run.

- [ ] **Step 5: Document how to run the suite**

Append to `README.md`, after the existing "To run the MCP tests directly" block (around line 132):

```markdown

To run the Web end-to-end tests (requires SQL Server LocalDB; starts and stops `HrDashboard.Web` automatically):

```powershell
dotnet test .\tests\HrDashboard.Web.E2E.Tests
```

One-time setup, after the first build, to download the Chromium browser Playwright drives:

```powershell
pwsh .\tests\HrDashboard.Web.E2E.Tests\bin\Debug\net10.0\playwright.ps1 install chromium
```
```

- [ ] **Step 6: Commit**

```bash
git add tests/HrDashboard.Web.E2E.Tests/HrDashboard.Web.E2E.Tests.csproj \
        HrDashboard.slnx \
        README.md
git commit -m "test: scaffold HrDashboard.Web.E2E.Tests project (Playwright .NET, NUnit)"
```

---

### Task 2: App-lifecycle fixture + auth test scenarios

**Files:**
- Create: `tests/HrDashboard.Web.E2E.Tests/WebAppFixture.cs`
- Create: `tests/HrDashboard.Web.E2E.Tests/AuthTests.cs`

**Interfaces:**
- Produces: `WebAppFixture.BaseUrl` (`const string`) — the running app's base URL, used by every test
- Consumes: `Microsoft.Playwright.NUnit.PageTest` (from the `Microsoft.Playwright.NUnit` package) — supplies `Page` (`IPage`) and `Expect(...)` per test

- [ ] **Step 1: Write the app-lifecycle fixture**

Create `tests/HrDashboard.Web.E2E.Tests/WebAppFixture.cs`:

```csharp
using System.Diagnostics;
using System.Net.Http;
using NUnit.Framework;

namespace HrDashboard.Web.E2E.Tests;

/// <summary>
/// Starts HrDashboard.Web as a real subprocess before the suite runs and stops it
/// after. Playwright drives a real browser against a real server — Blazor
/// InteractiveServer depends on a live SignalR circuit, so there is no in-memory
/// TestServer option here.
/// </summary>
[SetUpFixture]
public class WebAppFixture
{
    public const string BaseUrl = "http://localhost:5100";

    private static Process? _process;

    [OneTimeSetUp]
    public async Task StartWebAppAsync()
    {
        var repoRoot = FindRepoRoot();
        var webProjectPath = Path.Combine(repoRoot, "src", "HrDashboard.Web", "HrDashboard.Web.csproj");

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project \"{webProjectPath}\" --no-launch-profile --urls {BaseUrl}",
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.EnvironmentVariables["ASPNETCORE_ENVIRONMENT"] = "Development";

        _process = new Process { StartInfo = startInfo };
        _process.OutputDataReceived += (_, e) => { if (e.Data is not null) Console.WriteLine($"[web] {e.Data}"); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.WriteLine($"[web:err] {e.Data}"); };
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        await WaitUntilReadyAsync();
    }

    [OneTimeTearDown]
    public void StopWebApp()
    {
        if (_process is null) return;

        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }

    private static async Task WaitUntilReadyAsync()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow.AddSeconds(60);

        while (DateTime.UtcNow < deadline)
        {
            if (_process is { HasExited: true })
                throw new InvalidOperationException(
                    $"HrDashboard.Web exited early with code {_process.ExitCode}. " +
                    "Check that SQL Server LocalDB is installed and reachable.");

            try
            {
                var response = await client.GetAsync($"{BaseUrl}/login");
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException)
            {
                // Server not accepting connections yet — keep polling.
            }

            await Task.Delay(500);
        }

        throw new TimeoutException(
            $"HrDashboard.Web did not become ready at {BaseUrl} within 60 seconds. " +
            "Check that SQL Server LocalDB is installed and reachable.");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HrDashboard.slnx")))
            dir = dir.Parent;

        return dir?.FullName
            ?? throw new InvalidOperationException(
                "Could not locate HrDashboard.slnx above " + AppContext.BaseDirectory);
    }
}
```

- [ ] **Step 2: Write the auth test scenarios**

First, confirm the exact behavior these tests assert against, from `src/HrDashboard.Web/Program.cs`:
- Successful register or login → redirects to `/` and signs the user in (registering auto-signs-in, per `Program.cs:175-181`)
- Failed login → redirects to `/login?error=invalid&email=...`, and `Login.razor` renders a `MudAlert` with the text `Invalid email or password.`
- Logout is a `POST /account/logout` triggered by a `MudIconButton` with `title="Sign out"` inside `ConversationList.razor`, redirecting to `/login`

Create `tests/HrDashboard.Web.E2E.Tests/AuthTests.cs`. Page-state assertions use Playwright's own `Expect(...)` (inherited from `PageTest`) rather than FluentAssertions, because it auto-retries until the assertion holds or a timeout elapses — necessary here since a form submit triggers an async server-side redirect, not an instantly-available value. FluentAssertions remains in the project for any future plain-value assertions.

```csharp
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;
using NUnit.Framework;

namespace HrDashboard.Web.E2E.Tests;

public class AuthTests : PageTest
{
    private const string Password = "Passw0rd!";

    private static string UniqueEmail() => $"e2e-{Guid.NewGuid():N}@test.local";

    private async Task RegisterAsync(string email, string password)
    {
        await Page.GotoAsync($"{WebAppFixture.BaseUrl}/register");
        await Page.GetByLabel("Email").FillAsync(email);
        await Page.GetByLabel("Password", new() { Exact = true }).FillAsync(password);
        await Page.GetByLabel("Confirm password").FillAsync(password);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Register" }).ClickAsync();
    }

    private async Task LoginAsync(string email, string password)
    {
        await Page.GotoAsync($"{WebAppFixture.BaseUrl}/login");
        await Page.GetByLabel("Email").FillAsync(email);
        await Page.GetByLabel("Password", new() { Exact = true }).FillAsync(password);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
    }

    [Test]
    public async Task Register_NewUser_Succeeds()
    {
        var email = UniqueEmail();

        await RegisterAsync(email, Password);

        // Registering signs the user in immediately and redirects to the dashboard root.
        await Expect(Page).ToHaveURLAsync($"{WebAppFixture.BaseUrl}/");
    }

    [Test]
    public async Task Login_ValidCredentials_ReachesDashboard()
    {
        var email = UniqueEmail();
        await RegisterAsync(email, Password);
        await Expect(Page).ToHaveURLAsync($"{WebAppFixture.BaseUrl}/");

        // Sign out the freshly-registered (auto-signed-in) user, then log back in explicitly.
        await Page.GetByRole(AriaRole.Button, new() { Name = "Sign out" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync($"{WebAppFixture.BaseUrl}/login");

        await LoginAsync(email, Password);

        await Expect(Page).ToHaveURLAsync($"{WebAppFixture.BaseUrl}/");
    }

    [Test]
    public async Task Login_InvalidCredentials_ShowsError()
    {
        await LoginAsync(UniqueEmail(), "wrong-password");

        await Expect(Page).ToHaveURLAsync(new Regex(@"/login\?error=invalid"));
        await Expect(Page.GetByText("Invalid email or password.")).ToBeVisibleAsync();
    }

    [Test]
    public async Task Logout_SignedInUser_ReturnsToLogin()
    {
        var email = UniqueEmail();
        await RegisterAsync(email, Password);
        await Expect(Page).ToHaveURLAsync($"{WebAppFixture.BaseUrl}/");

        await Page.GetByRole(AriaRole.Button, new() { Name = "Sign out" }).ClickAsync();

        await Expect(Page).ToHaveURLAsync($"{WebAppFixture.BaseUrl}/login");
    }
}
```

- [ ] **Step 3: Build — verify 0 errors**

```powershell
dotnet build tests/HrDashboard.Web.E2E.Tests/HrDashboard.Web.E2E.Tests.csproj
```

Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 4: Run the suite — verify 4 pass**

Requires SQL Server LocalDB reachable (same prerequisite `HrDashboard.Web` already has) and the Chromium browser installed (Task 1, Step 4).

```powershell
dotnet test tests/HrDashboard.Web.E2E.Tests -v normal
```

Expected: `Passed! - Failed: 0, Passed: 4, Skipped: 0`.

If the fixture times out waiting for the app to become ready, check:
- SQL Server LocalDB is installed and running (`sqllocaldb info mssqllocaldb`)
- Port 5100 isn't already bound by another process
- The console output prefixed `[web:err]` in the test output for the app's own startup errors

- [ ] **Step 5: Run the full solution test suite — confirm no regressions**

```powershell
dotnet test
```

Expected: all test projects pass, including the pre-existing 27 (McpServer.Tests, Agents.Tests, Infrastructure.Tests) plus these 4 new ones — 31 total.

- [ ] **Step 6: Commit**

```bash
git add tests/HrDashboard.Web.E2E.Tests/WebAppFixture.cs \
        tests/HrDashboard.Web.E2E.Tests/AuthTests.cs
git commit -m "test: add Web E2E auth flow tests — register, login, invalid login, logout"
```

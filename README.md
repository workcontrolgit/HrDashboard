# HrDashboard

HrDashboard is an HR analytics dashboard that lets authenticated users ask natural-language questions about HR data. The web application sends agent requests through an MCP server, whose tools query HR data via a database bridge that supports either Oracle or SQL Server, selected by configuration. Conversations and user accounts are stored in SQL Server.

## Architecture

```text
Blazor Web App
    |
    | IHrAgentService / IChatClient
    v
HrDashboard.McpServer  (HTTP: http://localhost:5200/mcp)
    |
    | OracleAnalyticsTools / SqlServerAnalyticsTools, HrSchemaTools
    v
IHrDataBridge  (OracleBridge or SqlServerBridge)
    |
    v
Configured HR database (Oracle or SQL Server)
```

The solution is organized into these projects:

| Project | Responsibility |
| --- | --- |
| `HrDashboard.Web` | Blazor interactive server UI, authentication, chat sessions, and SQL Server persistence |
| `HrDashboard.Agents` | Agent service and conversation context abstractions |
| `HrDashboard.Infrastructure` | EF Core `AppDbContext`, ASP.NET Core Identity entities, conversations, messages, and migrations |
| `HrDashboard.McpServer` | MCP HTTP/stdio server, hosting the HR analytics/schema-discovery tools and the Oracle/SQL Server bridges |
| `HrDashboard.McpServer.Tests` | Unit tests for MCP tools and the Oracle/SQL Server bridges |

## Prerequisites

- Windows
- .NET 10 SDK
- SQL Server LocalDB, or another SQL Server instance
- An Oracle or SQL Server database containing the HR data, reachable via the connection string in `ConnectionStrings:HrData`
- One of:
  - Ollama running locally with the configured model, or
  - Azure OpenAI credentials and an accessible deployment

## Configuration

### Web application

Development defaults are in `src/HrDashboard.Web/appsettings.Development.json`:

- SQL Server: `(localdb)\mssqllocaldb`, database `HrDashboard`
- AI provider: Ollama
- Ollama endpoint: `http://localhost:11434`
- Ollama model: `qwen3-vl:8b`
- MCP endpoint: `http://localhost:5200/mcp`

Keep credentials out of committed JSON files. Use ASP.NET Core user secrets for local secrets:

```powershell
dotnet user-secrets set "AI:AzureOpenAI:Endpoint" "https://<resource>.openai.azure.com/" --project .\src\HrDashboard.Web
dotnet user-secrets set "AI:AzureOpenAI:ApiKey" "<api-key>" --project .\src\HrDashboard.Web
```

To use Azure OpenAI instead of Ollama, set:

```text
AI:Provider=AzureOpenAI
AI:AzureOpenAI:Endpoint=https://<resource>.openai.azure.com/
AI:AzureOpenAI:DeploymentName=<deployment-name>
```

The application uses `DefaultAzureCredential` when an Azure OpenAI API key is not provided. In production, `AzureKeyVault:VaultUri` can be configured so the web app loads secrets from Azure Key Vault.

### MCP server and database provider

Set the data provider and connection string in `src/HrDashboard.McpServer/appsettings.json`:

```json
{
  "Database": {
    "Provider": "Oracle",
    "VisibleTables": "EMPLOYEES,DEPARTMENTS,JOBS,LOCATIONS"
  },
  "ConnectionStrings": {
    "HrData": "User Id=hr;Password=...;Data Source=localhost:1521/XEPDB1;"
  },
  "Urls": "http://localhost:5200"
}
```

`Database:Provider` can be `"Oracle"` or `"SqlServer"`. Switching providers also requires a matching `ConnectionStrings:HrData` value — for example, a SQL Server target: `Server=(localdb)\mssqllocaldb;Database=HrData;Trusted_Connection=True;`.

The MCP server can run over HTTP for the web app or over stdio for MCP clients:

```powershell
dotnet run --project .\src\HrDashboard.McpServer -- --stdio
```

## Run locally

Start the MCP server first in one terminal:

```powershell
dotnet run --project .\src\HrDashboard.McpServer
```

Then start the web app in a second terminal:

```powershell
dotnet run --project .\src\HrDashboard.Web
```

Open the HTTPS URL printed by ASP.NET Core. On first startup, the web application automatically applies the EF Core migrations and creates the `HrDashboard` database in LocalDB.

Register an account, sign in, and use the dashboard chat to ask questions supported by the HR analytics tools.

## Test and build

Restore and build the solution:

```powershell
dotnet restore .\HrDashboard.slnx
dotnet build .\HrDashboard.slnx
```

Run the test projects included in the solution:

```powershell
dotnet test .\HrDashboard.slnx
```

Note: the E2E project below has extra local prerequisites (SQL Server LocalDB, Chromium) — see the "Web end-to-end tests" section further down before running the full solution suite if you haven't set those up.

To run the MCP tests directly:

```powershell
dotnet test .\tests\HrDashboard.McpServer.Tests
```

To run the Web end-to-end tests (requires SQL Server LocalDB; starts and stops `HrDashboard.Web` automatically):

```powershell
dotnet test .\tests\HrDashboard.Web.E2E.Tests
```

Note: 3 of the 4 tests in this project are currently tagged `[Fact(Skip = "...")]`, pending a tracked application bug (bug-023 in `.wolf/buglog.json`) unrelated to the test infrastructure — a run reporting "Skipped: 3" is expected, not a sign anything is broken in your setup.

To run the other test projects without the E2E project's extra prerequisites (LocalDB, Chromium) and without the provider-parity integration tests:

```powershell
dotnet test --filter "Category!=E2E&Category!=Integration"
```

Note: the `Integration`-tagged tests (`ProviderParityTests`) require a live, reachable Oracle database and a populated SQL Server `HrData` database (cloned via `tools/HrDataClone`) to pass — analogous to the E2E project's LocalDB/Chromium prerequisites above.

One-time setup, after the first build, to download the Chromium browser Playwright drives:

```powershell
pwsh .\tests\HrDashboard.Web.E2E.Tests\bin\Debug\net10.0\playwright.ps1 install chromium
```

If E2E tests start failing after a `dotnet restore` or package update with an error about a Playwright driver/browser version mismatch, re-run the `playwright.ps1 install chromium` command above — this project's package versions float (`Version="1.*"`, matching house style), so a version bump can invalidate the previously-installed browser binaries.

## Logs

Both applications write rolling logs under the repository `logs` directory:

```text
logs/web/info/
logs/web/error/
logs/mcpserver/info/
logs/mcpserver/error/
```

Console output is also enabled for normal web and HTTP MCP server runs. The MCP server suppresses console logging in stdio mode so its protocol stream remains clean.

## Troubleshooting

- **The web app cannot connect to MCP:** start `HrDashboard.McpServer` first and confirm that `McpServer:Endpoint` matches its listening URL.
- **HR data queries fail:** confirm `Database:Provider` matches the intended database and that `ConnectionStrings:HrData` points to a reachable, correctly credentialed Oracle or SQL Server instance.
- **Ollama requests fail:** confirm Ollama is running and that the configured model has been pulled locally.
- **The database cannot be opened:** confirm SQL Server LocalDB is installed, or replace `ConnectionStrings:DefaultConnection` with a reachable SQL Server connection string.
- **The server exits immediately:** inspect the latest files under `logs/mcpserver/error` or `logs/web/error`.
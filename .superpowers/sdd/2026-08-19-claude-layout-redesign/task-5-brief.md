# Task 5 Brief: EF Migration + SQL Server connection string

## Context
Task 5 of 13. Adds EF packages to the Web project, adds connection string, runs the initial migration, and applies it to the local SQL Server (localdb).
Repository: c:/apps/HrDashboard, branch: feature/claude-layout-redesign.
`dotnet ef` 10.0.8 is installed globally.

## Global Constraints
- net10.0, EF Core packages version `10.*`
- Connection string key: `ConnectionStrings:DefaultConnection`
- Local dev connection string: `Server=(localdb)\\mssqllocaldb;Database=HrDashboard;Trusted_Connection=True;MultipleActiveResultSets=true`
- Migration name: `InitialCreate`
- No subagents — implement, build, commit, write report yourself

## Steps

### Step 1: Modify `src/HrDashboard.Web/HrDashboard.Web.csproj`

Add these three packages to the existing `<ItemGroup>` that already has packages (add alongside existing PackageReferences):
```xml
<PackageReference Include="Microsoft.EntityFrameworkCore.Tools" Version="10.*" />
<PackageReference Include="Microsoft.AspNetCore.Identity.EntityFrameworkCore" Version="10.*" />
<PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" Version="10.*" />
```

Add a NEW `<ItemGroup>` for the project reference:
```xml
<ItemGroup>
  <ProjectReference Include="..\HrDashboard.Infrastructure\HrDashboard.Infrastructure.csproj" />
</ItemGroup>
```

Note: There is already a `<ProjectReference Include="..\HrDashboard.Agents\HrDashboard.Agents.csproj" />` in the file. Add the Infrastructure reference in a new ItemGroup or alongside the existing one.

### Step 2: Add connection string to `src/HrDashboard.Web/appsettings.Development.json`

The current content is:
```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Debug",
      "Override": {
        "Microsoft.AspNetCore": "Warning",
        "Microsoft.EntityFrameworkCore": "Warning"
      }
    }
  },
  "AI": {
    "Provider": "Ollama",
    "Ollama": {
      "Endpoint": "http://localhost:11434",
      "Model": "llama3.1"
    }
  }
}
```

Replace it with (adds `ConnectionStrings` section, keeps everything else):
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=(localdb)\\mssqllocaldb;Database=HrDashboard;Trusted_Connection=True;MultipleActiveResultSets=true"
  },
  "Serilog": {
    "MinimumLevel": {
      "Default": "Debug",
      "Override": {
        "Microsoft.AspNetCore": "Warning",
        "Microsoft.EntityFrameworkCore": "Warning"
      }
    }
  },
  "AI": {
    "Provider": "Ollama",
    "Ollama": {
      "Endpoint": "http://localhost:11434",
      "Model": "llama3.1"
    }
  }
}
```

### Step 3: Temporarily add AppDbContext registration to `src/HrDashboard.Web/Program.cs`

Read the existing Program.cs first. Add these lines BEFORE the existing `builder.Services.AddRazorComponents()` line:

```csharp
// TEMP: DbContext registration for EF migration scaffolding — replaced by Task 6
var connStr = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:DefaultConnection");
builder.Services.AddDbContext<HrDashboard.Infrastructure.AppDbContext>(o =>
    o.UseSqlServer(connStr));
```

Also add this using at the top of Program.cs (after existing usings):
```csharp
using Microsoft.EntityFrameworkCore;
```

### Step 4: Build the Web project
```bash
dotnet build src/HrDashboard.Web/HrDashboard.Web.csproj
```
Expected: Build succeeded, 0 errors.

### Step 5: Run EF migration
```bash
dotnet ef migrations add InitialCreate \
  --project src/HrDashboard.Infrastructure/HrDashboard.Infrastructure.csproj \
  --startup-project src/HrDashboard.Web/HrDashboard.Web.csproj \
  --context HrDashboard.Infrastructure.AppDbContext
```
Expected: "Done. To undo this action, use 'ef migrations remove'"

### Step 6: Apply migration to local database
```bash
dotnet ef database update \
  --project src/HrDashboard.Infrastructure/HrDashboard.Infrastructure.csproj \
  --startup-project src/HrDashboard.Web/HrDashboard.Web.csproj \
  --context HrDashboard.Infrastructure.AppDbContext
```
Expected: "Done."

### Step 7: Commit
```bash
git add src/HrDashboard.Infrastructure/Migrations/ \
        src/HrDashboard.Web/HrDashboard.Web.csproj \
        src/HrDashboard.Web/appsettings.Development.json \
        src/HrDashboard.Web/Program.cs
git commit -m "feat(infra): add EF migration InitialCreate for Identity + Conversations + Messages"
```

### Step 8: Write report
Write to: `.superpowers/sdd/2026-08-19-claude-layout-redesign/task-5-report.md`

## Report Contract
Status: DONE | DONE_WITH_CONCERNS | NEEDS_CONTEXT | BLOCKED
Commits: <sha>
Test summary: build + migration result
Concerns: (if any)

## Troubleshooting
- If `dotnet ef migrations add` fails with "Unable to create a 'DbContext'", ensure Step 3 (temp registration) was done and the build succeeded
- If localdb is not available, set Status to DONE_WITH_CONCERNS and note that migration was generated but not applied

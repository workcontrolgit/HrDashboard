# Task 3 Brief: HrDashboard.Infrastructure — project skeleton + entities

## Context
Task 3 of 13. Creates a new class library project with EF Core, Identity, and the entity model.
Repository: c:/apps/HrDashboard, branch: feature/claude-layout-redesign.
Solution file: HrDashboard.slnx

## Global Constraints
- net10.0, nullable enabled, implicit usings enabled
- EF Core / Identity packages: version `10.*`
- No subagents — implement, build, commit, write report yourself

## Files to Create

### 1. `src/HrDashboard.Infrastructure/HrDashboard.Infrastructure.csproj`
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.Identity.EntityFrameworkCore" Version="10.*" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" Version="10.*" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\HrDashboard.Agents\HrDashboard.Agents.csproj" />
  </ItemGroup>
</Project>
```

### 2. Add project to solution
```bash
dotnet sln HrDashboard.slnx add src/HrDashboard.Infrastructure/HrDashboard.Infrastructure.csproj
```

### 3. `src/HrDashboard.Infrastructure/AppUser.cs`
```csharp
using Microsoft.AspNetCore.Identity;

namespace HrDashboard.Infrastructure;

public class AppUser : IdentityUser { }
```

### 4. `src/HrDashboard.Infrastructure/Entities/Conversation.cs`
```csharp
namespace HrDashboard.Infrastructure.Entities;

public class Conversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = string.Empty;
    public string Title { get; set; } = "New conversation";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<Message> Messages { get; set; } = [];
}
```

### 5. `src/HrDashboard.Infrastructure/Entities/Message.cs`
```csharp
using HrDashboard.Agents.Models;

namespace HrDashboard.Infrastructure.Entities;

public class Message
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public Conversation Conversation { get; set; } = null!;
    public MessageRole Role { get; set; }
    public string Content { get; set; } = string.Empty;
    /// <summary>Serialized List&lt;HrMetricRow&gt;. Display only — never sent to agent.</summary>
    public string? MetricsJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
```

### 6. `src/HrDashboard.Infrastructure/AppDbContext.cs`
```csharp
using HrDashboard.Infrastructure.Entities;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace HrDashboard.Infrastructure;

public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser>(options)
{
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Conversation>(e =>
        {
            e.HasKey(c => c.Id);
            e.Property(c => c.UserId).HasMaxLength(450).IsRequired();
            e.Property(c => c.Title).HasMaxLength(512);
            e.HasMany(c => c.Messages)
             .WithOne(m => m.Conversation)
             .HasForeignKey(m => m.ConversationId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(c => c.UserId);
        });

        builder.Entity<Message>(e =>
        {
            e.HasKey(m => m.Id);
            e.Property(m => m.Content).IsRequired();
            e.Property(m => m.Role).HasConversion<string>();
        });
    }
}
```

## Steps
1. Create all 5 files above (csproj, AppUser, Conversation, Message, AppDbContext)
2. Run: `dotnet sln HrDashboard.slnx add src/HrDashboard.Infrastructure/HrDashboard.Infrastructure.csproj`
3. Run: `dotnet build src/HrDashboard.Infrastructure/HrDashboard.Infrastructure.csproj`
4. Verify: Build succeeded, 0 errors
5. Commit:
   ```
   git add src/HrDashboard.Infrastructure/ HrDashboard.slnx
   git commit -m "feat(infra): add Infrastructure project with AppDbContext, AppUser, and entities"
   ```
6. Write report to: `.superpowers/sdd/2026-08-19-claude-layout-redesign/task-3-report.md`

## Report Contract
Status: DONE | DONE_WITH_CONCERNS | NEEDS_CONTEXT | BLOCKED
Commits: <sha>
Test summary: build result (pass/fail, 0 errors)
Concerns: (if any)

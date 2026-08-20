using HrDashboard.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HrDashboard.Infrastructure.Tests;

/// <summary>
/// Creates a fresh in-memory AppDbContext per factory instance.
/// Each test creates its own factory → completely isolated DB.
/// </summary>
internal sealed class TestDbContextFactory : IDbContextFactory<AppDbContext>
{
    private readonly DbContextOptions<AppDbContext> _options;

    public TestDbContextFactory()
    {
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    public AppDbContext CreateDbContext() => new(_options);

    public Task<AppDbContext> CreateDbContextAsync(CancellationToken ct = default)
        => Task.FromResult(CreateDbContext());
}

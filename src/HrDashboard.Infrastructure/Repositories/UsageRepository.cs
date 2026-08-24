using HrDashboard.Agents;
using HrDashboard.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;

namespace HrDashboard.Infrastructure.Repositories;

public class UsageRepository(IDbContextFactory<AppDbContext> dbFactory) : IUsageRepository
{
    public async Task AddUsageRecordAsync(
        Guid messageId,
        string userId,
        string provider,
        string model,
        long inputTokens,
        long outputTokens,
        long totalTokens,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.UsageRecords.Add(new UsageRecord
        {
            MessageId = messageId,
            UserId = userId,
            Provider = provider,
            Model = model,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            TotalTokens = totalTokens
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<DailyUsageTotal>> GetDailyTotalsAsync(
        string userId, int days, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var cutoff = DateTime.UtcNow.Date.AddDays(-(days - 1));

        // Group/aggregate in SQL, then convert to DateOnly in memory — avoids relying on
        // the provider translating DateOnly.FromDateTime inside the query itself.
        var raw = await db.UsageRecords
            .Where(u => u.UserId == userId && u.CreatedAt >= cutoff)
            .GroupBy(u => u.CreatedAt.Date)
            .Select(g => new { Date = g.Key, Total = g.Sum(x => x.TotalTokens) })
            .ToListAsync(ct);

        return raw
            .Select(r => new DailyUsageTotal(DateOnly.FromDateTime(r.Date), r.Total))
            .OrderBy(d => d.Date)
            .ToList();
    }

    public async Task<List<ProviderModelUsageTotal>> GetProviderModelBreakdownAsync(
        string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.UsageRecords
            .Where(u => u.UserId == userId)
            .GroupBy(u => new { u.Provider, u.Model })
            .OrderByDescending(g => g.Sum(x => x.TotalTokens))
            .Select(g => new ProviderModelUsageTotal(
                g.Key.Provider,
                g.Key.Model,
                g.Sum(x => x.InputTokens),
                g.Sum(x => x.OutputTokens),
                g.Sum(x => x.TotalTokens),
                g.Count()))
            .ToListAsync(ct);
    }
}

namespace HrDashboard.Agents;

public interface IUsageRepository
{
    Task AddUsageRecordAsync(
        Guid messageId,
        string userId,
        string provider,
        string model,
        long inputTokens,
        long outputTokens,
        long totalTokens,
        CancellationToken ct = default);

    /// <summary>Total tokens per calendar day, over the last <paramref name="days"/> days, oldest first.</summary>
    Task<List<DailyUsageTotal>> GetDailyTotalsAsync(
        string userId, int days, CancellationToken ct = default);

    /// <summary>All-time totals grouped by provider + model.</summary>
    Task<List<ProviderModelUsageTotal>> GetProviderModelBreakdownAsync(
        string userId, CancellationToken ct = default);
}

public record DailyUsageTotal(DateOnly Date, long TotalTokens);

public record ProviderModelUsageTotal(
    string Provider,
    string Model,
    long InputTokens,
    long OutputTokens,
    long TotalTokens,
    int TurnCount);

using HrDashboard.Agents.Models;

namespace HrDashboard.Agents;

public interface IHrAgentService
{
    /// <summary>
    /// Submits a natural language HR analytics query and returns structured metric rows.
    /// </summary>
    Task<(string RawText, IReadOnlyList<HrMetricRow> Metrics)> AskAsync(
        string prompt,
        CancellationToken ct = default);
}

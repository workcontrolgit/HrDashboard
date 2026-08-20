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

    /// <summary>
    /// Streams the AI response chunk by chunk. Runs the tool-use loop internally
    /// (non-streaming), then yields the final text as it arrives.
    /// history = prior (Role, Content) pairs for context — MetricsJson is never included.
    /// </summary>
    IAsyncEnumerable<string> AskStreamAsync(
        IEnumerable<(MessageRole Role, string Content)> history,
        string prompt,
        CancellationToken ct = default);
}

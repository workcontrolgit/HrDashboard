using HrDashboard.Agents.Models;

namespace HrDashboard.Agents;

public interface IHrAgentService
{
    /// <summary>
    /// Submits a natural language HR analytics query and returns structured metric rows.
    /// PendingColumns is populated instead of Metrics when the model asked a listing-
    /// column clarification question rather than returning a final data answer.
    /// </summary>
    Task<(string RawText, IReadOnlyList<HrMetricRow> Metrics, PendingColumnOptions? PendingColumns)> AskAsync(
        string prompt,
        CancellationToken ct = default);

    /// <summary>
    /// Streams the AI response chunk by chunk. Runs the tool-use loop internally
    /// (non-streaming), then yields the final text as it arrives.
    /// history = prior (Role, Content) pairs for context — MetricsJson is never included.
    /// Once the returned stream is fully drained, <see cref="LastPendingColumnOptions"/>
    /// reflects whether this turn ended in a listing-column clarification.
    /// </summary>
    IAsyncEnumerable<string> AskStreamAsync(
        IEnumerable<(MessageRole Role, string Content)> history,
        string prompt,
        CancellationToken ct = default);

    /// <summary>
    /// Set by the most recent <see cref="AskStreamAsync"/> call once its stream is fully
    /// drained. Null for a normal final answer or a purely conversational response;
    /// populated when the model asked a listing-column clarification question instead
    /// of calling a data tool, grounded in a real DescribeTable result.
    /// </summary>
    PendingColumnOptions? LastPendingColumnOptions { get; }

    /// <summary>
    /// Returns the real table/column schema available to query, built directly from the
    /// ListTables/DescribeTable tool results — no LLM call involved, so this costs zero
    /// tokens and the columns can never be hallucinated. Intended for a chat-start
    /// "what data is available" summary shown alongside the preset chips.
    /// </summary>
    Task<IReadOnlyList<TableOverview>> GetSchemaOverviewAsync(CancellationToken ct = default);
}

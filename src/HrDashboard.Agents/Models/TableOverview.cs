namespace HrDashboard.Agents.Models;

/// <summary>
/// One table's real column names, for the data-availability summary shown when a chat
/// starts — built directly from schema-discovery tool results, with no LLM call
/// involved, so it costs zero tokens and can never be hallucinated.
/// </summary>
public sealed record TableOverview(string TableName, IReadOnlyList<string> Columns);

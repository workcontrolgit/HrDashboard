using HrDashboard.Agents.Models;
using Microsoft.Extensions.AI;

namespace HrDashboard.Agents;

/// <summary>
/// Tracks which tools were invoked during one agent turn, so the turn can be classified
/// as a listing-column clarification (only schema tools called, no data returned) versus
/// a normal final answer — without parsing the model's own prose. Grounds the eventual
/// <see cref="PendingColumnOptions"/> in the real DescribeTable tool result, not in
/// anything the model wrote, so offered columns can never be hallucinated.
/// </summary>
internal sealed class ToolCallTracker
{
    public bool DataToolCalled { get; private set; }
    public string? LastDescribeTableName { get; private set; }
    public IReadOnlyList<string>? LastDescribeTableColumns { get; private set; }

    public void Observe(FunctionCallContent call, object? result)
    {
        var isSchemaTool =
            string.Equals(call.Name, "ListTables", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(call.Name, "DescribeTable", StringComparison.OrdinalIgnoreCase);

        if (!isSchemaTool)
        {
            DataToolCalled = true;
            return;
        }

        if (!string.Equals(call.Name, "DescribeTable", StringComparison.OrdinalIgnoreCase))
            return;

        var json = SchemaJsonParser.ExtractStringResult(result);
        if (json is null || !TryParseColumnNames(json, out var columns))
            return;

        LastDescribeTableName = call.Arguments is not null
            && call.Arguments.TryGetValue("tableName", out var tableName)
            ? tableName?.ToString() ?? ""
            : "";
        LastDescribeTableColumns = columns;
    }

    /// <summary>
    /// Returns the real column list to offer as a clarification, or null if this turn
    /// was a normal final answer (a data tool ran, or no DescribeTable columns were
    /// captured, or the response text isn't plain natural language).
    /// </summary>
    public PendingColumnOptions? Classify(string finalText)
    {
        if (DataToolCalled) return null;
        if (LastDescribeTableColumns is null || LastDescribeTableColumns.Count == 0) return null;
        if (!HrMetricParser.LooksLikeGenuineTextAnswer(finalText)) return null;

        return new PendingColumnOptions(LastDescribeTableName ?? "", LastDescribeTableColumns);
    }

    // DescribeTable's JSON result is a row-per-column array (see DbResultSerializer in
    // HrDashboard.McpServer). Delegates to the shared parser also used by
    // GetSchemaOverviewAsync (see SchemaJsonParser.cs).
    private static bool TryParseColumnNames(string json, out IReadOnlyList<string> columns) =>
        SchemaJsonParser.TryParseRowValues(json, "column_name", out columns);
}

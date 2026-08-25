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

    // The raw result of the most recent non-schema ("data") tool call this turn, kept so
    // HrAgentService can build the final HrDataSet directly from the real query result
    // instead of asking the model to re-transcribe every row as its own output — the model
    // only needs to supply a title and chart recommendation, not the data itself. This
    // keeps token cost flat regardless of row count and removes the output-truncation
    // failure mode entirely for large results (see bug: raw JSON leaking into the chat
    // bubble when the model ran out of output length mid-transcription).
    public string? LastDataToolName { get; private set; }
    public string? LastDataToolResult { get; private set; }

    // Accumulates EVERY DescribeTable call in the turn (not just the last), so a listing
    // that spans a foreign-key relationship (e.g. departments + their manager's name from
    // EMPLOYEES) can offer columns from all described tables — grounded in real schema
    // calls the same way a single-table listing always was, just no longer limited to one
    // table's columns silently overwriting another's.
    private readonly List<string> _describedTableOrder = [];
    private readonly Dictionary<string, IReadOnlyList<string>> _describedTableColumns =
        new(StringComparer.OrdinalIgnoreCase);

    public void Observe(FunctionCallContent call, object? result)
    {
        var isSchemaTool =
            string.Equals(call.Name, "ListTables", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(call.Name, "DescribeTable", StringComparison.OrdinalIgnoreCase);

        if (!isSchemaTool)
        {
            DataToolCalled = true;
            LastDataToolName = call.Name;
            LastDataToolResult = SchemaJsonParser.ExtractStringResult(result);
            return;
        }

        if (!string.Equals(call.Name, "DescribeTable", StringComparison.OrdinalIgnoreCase))
            return;

        var json = SchemaJsonParser.ExtractStringResult(result);
        if (json is null || !TryParseColumnNames(json, out var columns))
            return;

        var tableName = call.Arguments is not null
            && call.Arguments.TryGetValue("tableName", out var tableNameArg)
            ? tableNameArg?.ToString() ?? ""
            : "";

        LastDescribeTableName = tableName;
        LastDescribeTableColumns = columns;

        if (!_describedTableColumns.ContainsKey(tableName))
            _describedTableOrder.Add(tableName);
        _describedTableColumns[tableName] = columns;
    }

    /// <summary>
    /// Returns the real column list to offer as a clarification, or null if this turn
    /// was a normal final answer (a data tool ran, or no DescribeTable columns were
    /// captured, or the response text isn't plain natural language). When more than one
    /// table was described this turn, each column is qualified as "TABLE.COLUMN" — both a
    /// display disambiguator (two HR tables can share a raw name, e.g. MANAGER_ID) and
    /// valid SQL the model can use verbatim once the user confirms their selection.
    /// </summary>
    public PendingColumnOptions? Classify(string finalText)
    {
        if (DataToolCalled) return null;
        if (_describedTableOrder.Count == 0) return null;
        if (!HrMetricParser.LooksLikeGenuineTextAnswer(finalText)) return null;

        var multiTable = _describedTableOrder.Count > 1;
        var combinedColumns = _describedTableOrder
            .SelectMany(table => _describedTableColumns[table]
                .Select(column => multiTable ? $"{table}.{column}" : column))
            .ToList();

        return new PendingColumnOptions(string.Join(", ", _describedTableOrder), combinedColumns);
    }

    // DescribeTable's JSON result is a row-per-column array (see DbResultSerializer in
    // HrDashboard.McpServer). Delegates to the shared parser also used by
    // GetSchemaOverviewAsync (see SchemaJsonParser.cs).
    private static bool TryParseColumnNames(string json, out IReadOnlyList<string> columns) =>
        SchemaJsonParser.TryParseRowValues(json, "column_name", out columns);
}

using System.Data.Common;
using System.Text.Json;

namespace HrDashboard.McpServer;

/// <summary>
/// Shared row-to-JSON serialization for both bridges. A single-row/single-column
/// result (the shape every curated tool's own JSON_OBJECT/FOR JSON query already
/// produces) is returned as-is rather than re-wrapped, so curated-tool output is
/// never double-JSON-encoded. Anything else (ad hoc queries, schema discovery)
/// is serialized as a JSON array of row objects.
/// </summary>
internal static class DbResultSerializer
{
    public const int MaxRows = 200;

    public static async Task<string> ReadAsJsonAsync(DbDataReader reader, CancellationToken ct)
    {
        var rows = new List<Dictionary<string, object?>>();

        while (rows.Count < MaxRows && await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }

        if (rows.Count == 1 && rows[0].Count == 1 && rows[0].Values.First() is string singleCell)
            return singleCell;

        return JsonSerializer.Serialize(rows);
    }
}

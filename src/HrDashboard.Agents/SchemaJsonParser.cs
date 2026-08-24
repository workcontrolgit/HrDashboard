using System.Text.Json;
using Microsoft.Extensions.AI;

namespace HrDashboard.Agents;

/// <summary>
/// Shared parsing for the row-per-record JSON that HrDashboard.McpServer's schema tools
/// (ListTables, DescribeTable) return (see DbResultSerializer in that project), e.g.
/// [{"table_name":"EMPLOYEES"},...] or [{"column_name":"EMPLOYEE_ID",...},...]. The key
/// name's case can vary by provider (Oracle vs SQL Server), hence
/// PropertyNameCaseInsensitive. Non-JSON error strings like "[SQL error: ...]" fail to
/// parse and correctly yield no values rather than throwing.
/// </summary>
internal static class SchemaJsonParser
{
    public static bool TryParseRowValues(string json, string columnKey, out IReadOnlyList<string> values)
    {
        values = [];
        try
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var rows = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(json, opts);
            if (rows is null || rows.Count == 0) return false;

            var results = new List<string>();
            foreach (var row in rows)
            {
                var key = row.Keys.FirstOrDefault(k => string.Equals(k, columnKey, StringComparison.OrdinalIgnoreCase));
                if (key is null) return false;

                var value = row[key].ValueKind == JsonValueKind.String ? row[key].GetString() : null;
                if (!string.IsNullOrWhiteSpace(value)) results.Add(value);
            }

            if (results.Count == 0) return false;
            values = results;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // A tool result reaches this codebase three different ways depending on the
    // concrete AIFunction source: (1) a real MCP-derived tool (via ModelContextProtocol.Client)
    // returns TextContent, confirmed by live testing against a real MCP server; (2) a raw string
    // is another production path; (3) an AIFunctionFactory.Create-built delegate (used
    // throughout this project's own tests as a fake tool) wraps its return value as a
    // System.Text.Json.JsonElement instead — confirmed by direct inspection against the
    // installed Microsoft.Extensions.AI.Abstractions 10.9.0 package. Every caller that reads
    // a tool's raw JSON text should go through this helper rather than assuming one exact CLR
    // type, the same defensive-parsing posture this project already applies to LLM-sourced
    // HrMetricRow fields (see FlexibleStringConverter).
    public static string? ExtractStringResult(object? result) => result switch
    {
        string s => s,
        TextContent tc => tc.Text,
        JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
        _ => null
    };
}

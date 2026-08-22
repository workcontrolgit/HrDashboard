using System.ComponentModel;
using HrDashboard.McpServer;
using ModelContextProtocol.Server;

namespace HrDashboard.McpServer.Tools;

[McpServerToolType]
public sealed class HrSchemaTools(IHrDataBridge bridge)
{
    [McpServerTool(Name = "ListTables"),
     Description("Lists the HR tables available to query. Call this before RunHrQuery if you don't already know the exact table/column names you need.")]
    public async Task<string> ListTables(CancellationToken ct = default)
        => await bridge.ListTablesAsync(ct);

    [McpServerTool(Name = "DescribeTable"),
     Description("Returns the column names and types for one HR table. Call this before RunHrQuery to confirm exact column names.")]
    public async Task<string> DescribeTable(
        [Description("Table name, e.g. EMPLOYEES")] string tableName,
        CancellationToken ct = default)
        => await bridge.DescribeTableAsync(tableName, ct);
}

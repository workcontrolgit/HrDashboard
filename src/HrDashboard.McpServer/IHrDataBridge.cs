namespace HrDashboard.McpServer;

public interface IHrDataBridge
{
    Task<string> RunSqlAsync(string sql, CancellationToken ct = default);
    Task<string> ListTablesAsync(CancellationToken ct = default);
    Task<string> DescribeTableAsync(string tableName, CancellationToken ct = default);
}

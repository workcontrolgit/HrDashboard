using HrDashboard.McpServer;

namespace HrDashboard.McpServer.Tests;

internal sealed class FakeHrDataBridge(string runSqlReturnValue = "[]") : IHrDataBridge
{
    public string? LastSql { get; private set; }
    public int CallCount { get; private set; }
    public string? LastDescribedTable { get; private set; }
    public int ListTablesCallCount { get; private set; }
    public int DescribeTableCallCount { get; private set; }

    public string ListTablesReturnValue { get; set; } = "[]";
    public string DescribeTableReturnValue { get; set; } = "[]";

    public Task<string> RunSqlAsync(string sql, CancellationToken ct = default)
    {
        LastSql = sql;
        CallCount++;
        return Task.FromResult(runSqlReturnValue);
    }

    public Task<string> ListTablesAsync(CancellationToken ct = default)
    {
        ListTablesCallCount++;
        return Task.FromResult(ListTablesReturnValue);
    }

    public Task<string> DescribeTableAsync(string tableName, CancellationToken ct = default)
    {
        LastDescribedTable = tableName;
        DescribeTableCallCount++;
        return Task.FromResult(DescribeTableReturnValue);
    }
}

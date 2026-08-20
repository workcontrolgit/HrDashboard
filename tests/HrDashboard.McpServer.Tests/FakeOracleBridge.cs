using HrDashboard.McpServer;

namespace HrDashboard.McpServer.Tests;

internal sealed class FakeOracleBridge(string returnValue = "[]") : IOracleBridge
{
    public string? LastSql { get; private set; }
    public int CallCount { get; private set; }

    public Task<string> RunSqlAsync(string sql, CancellationToken ct = default)
    {
        LastSql = sql;
        CallCount++;
        return Task.FromResult(returnValue);
    }
}

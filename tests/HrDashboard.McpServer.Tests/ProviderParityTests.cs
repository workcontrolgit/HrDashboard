using System.Text.Json;
using FluentAssertions;
using HrDashboard.McpServer.Tools;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace HrDashboard.McpServer.Tests;

[Trait("Category", "Integration")]
public class ProviderParityTests
{
    private static IConfiguration OracleConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:HrData"] = "User Id=hr;Password=HrUser_2026;Data Source=localhost:1521/XEPDB1;"
        })
        .Build();

    private static IConfiguration SqlServerConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:HrData"] = "Server=(localdb)\\mssqllocaldb;Database=HrData;Trusted_Connection=True;"
        })
        .Build();

    [Fact]
    public async Task GetDeptHeadcount_OracleAndSqlServer_ReturnSameShapeAndCounts()
    {
        var oracleTools = new OracleAnalyticsTools(new OracleBridge(OracleConfig()));
        var sqlServerTools = new SqlServerAnalyticsTools(new SqlServerBridge(SqlServerConfig()));

        var oracleResult = await oracleTools.GetDeptHeadcount();
        var sqlServerResult = await sqlServerTools.GetDeptHeadcount();

        using var oracleJson = JsonDocument.Parse(oracleResult);
        using var sqlServerJson = JsonDocument.Parse(sqlServerResult);

        oracleJson.RootElement.GetArrayLength().Should().Be(sqlServerJson.RootElement.GetArrayLength());

        var oracleFirst = oracleJson.RootElement.EnumerateArray().First();
        var sqlServerFirst = sqlServerJson.RootElement.EnumerateArray().First();
        oracleFirst.TryGetProperty("label", out _).Should().BeTrue();
        oracleFirst.TryGetProperty("value", out _).Should().BeTrue();
        sqlServerFirst.TryGetProperty("label", out _).Should().BeTrue();
        sqlServerFirst.TryGetProperty("value", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetJobSalaryRanges_OracleAndSqlServer_ReturnSameRowCount()
    {
        var oracleTools = new OracleAnalyticsTools(new OracleBridge(OracleConfig()));
        var sqlServerTools = new SqlServerAnalyticsTools(new SqlServerBridge(SqlServerConfig()));

        var oracleResult = await oracleTools.GetJobSalaryRanges();
        var sqlServerResult = await sqlServerTools.GetJobSalaryRanges();

        using var oracleJson = JsonDocument.Parse(oracleResult);
        using var sqlServerJson = JsonDocument.Parse(sqlServerResult);

        oracleJson.RootElement.GetArrayLength().Should().Be(sqlServerJson.RootElement.GetArrayLength());
    }
}

using FluentAssertions;
using HrDashboard.McpServer.Tools;
using Xunit;

namespace HrDashboard.McpServer.Tests;

public class HrAnalyticsToolsTests
{
    [Fact]
    public async Task GetTopEarnersByDepartment_PassesSqlToBridge_ReturnsResult()
    {
        var fake = new FakeOracleBridge("[{\"label\":\"Alice\",\"value\":9000}]");
        var tools = new HrAnalyticsTools(fake);

        var result = await tools.GetTopEarnersByDepartment(topN: 5);

        fake.CallCount.Should().Be(1);
        fake.LastSql.Should().Contain("FETCH FIRST 5 ROWS ONLY");
        result.Should().Be("[{\"label\":\"Alice\",\"value\":9000}]");
    }

    [Fact]
    public async Task GetSalaryBreakdownByDepartment_PassesSqlToBridge_ReturnsResult()
    {
        var fake = new FakeOracleBridge("[{\"label\":\"IT\",\"value\":8000}]");
        var tools = new HrAnalyticsTools(fake);

        var result = await tools.GetSalaryBreakdownByDepartment();

        fake.CallCount.Should().Be(1);
        fake.LastSql.Should().Contain("AVG(e.salary)");
        result.Should().Be("[{\"label\":\"IT\",\"value\":8000}]");
    }

    [Fact]
    public async Task GetDeptHeadcount_PassesSqlToBridge_ReturnsResult()
    {
        var fake = new FakeOracleBridge("[{\"label\":\"HR\",\"value\":10}]");
        var tools = new HrAnalyticsTools(fake);

        var result = await tools.GetDeptHeadcount();

        fake.CallCount.Should().Be(1);
        fake.LastSql.Should().Contain("COUNT(*)");
        result.Should().Be("[{\"label\":\"HR\",\"value\":10}]");
    }

    [Fact]
    public async Task GetJobSalaryRanges_PassesSqlToBridge_ReturnsResult()
    {
        var fake = new FakeOracleBridge("[{\"label\":\"Manager\",\"value\":15000}]");
        var tools = new HrAnalyticsTools(fake);

        var result = await tools.GetJobSalaryRanges();

        fake.CallCount.Should().Be(1);
        fake.LastSql.Should().Contain("max_salary");
        result.Should().Be("[{\"label\":\"Manager\",\"value\":15000}]");
    }

    [Fact]
    public async Task RunHrQuery_SelectStatement_PassesToBridge()
    {
        var fake = new FakeOracleBridge("[{\"label\":\"test\",\"value\":1}]");
        var tools = new HrAnalyticsTools(fake);
        const string sql = "SELECT employee_id FROM employees";

        var result = await tools.RunHrQuery(sql);

        fake.CallCount.Should().Be(1);
        fake.LastSql.Should().Be(sql);
        result.Should().Be("[{\"label\":\"test\",\"value\":1}]");
    }

    [Fact]
    public async Task RunHrQuery_NonSelect_RejectsWithoutCallingBridge()
    {
        var fake = new FakeOracleBridge("should not be returned");
        var tools = new HrAnalyticsTools(fake);

        var result = await tools.RunHrQuery("DELETE FROM employees");

        fake.CallCount.Should().Be(0);
        result.Should().Be("[Rejected: only SELECT statements are permitted]");
    }

    [Fact]
    public async Task RunHrQuery_SelectWithLeadingWhitespace_Accepted()
    {
        var fake = new FakeOracleBridge("[]");
        var tools = new HrAnalyticsTools(fake);

        var result = await tools.RunHrQuery("   SELECT 1 FROM dual");

        fake.CallCount.Should().Be(1);
        result.Should().Be("[]");
    }
}

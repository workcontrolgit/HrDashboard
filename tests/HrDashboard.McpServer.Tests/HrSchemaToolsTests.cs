using FluentAssertions;
using HrDashboard.McpServer.Tools;
using Xunit;

namespace HrDashboard.McpServer.Tests;

public class HrSchemaToolsTests
{
    [Fact]
    public async Task ListTables_ReturnsBridgeResult()
    {
        var fake = new FakeHrDataBridge { ListTablesReturnValue = "[\"EMPLOYEES\",\"DEPARTMENTS\"]" };
        var tools = new HrSchemaTools(fake);

        var result = await tools.ListTables();

        fake.ListTablesCallCount.Should().Be(1);
        result.Should().Be("[\"EMPLOYEES\",\"DEPARTMENTS\"]");
    }

    [Fact]
    public async Task DescribeTable_PassesTableNameToBridge_ReturnsResult()
    {
        var fake = new FakeHrDataBridge { DescribeTableReturnValue = "[{\"COLUMN_NAME\":\"SALARY\"}]" };
        var tools = new HrSchemaTools(fake);

        var result = await tools.DescribeTable("EMPLOYEES");

        fake.DescribeTableCallCount.Should().Be(1);
        fake.LastDescribedTable.Should().Be("EMPLOYEES");
        result.Should().Be("[{\"COLUMN_NAME\":\"SALARY\"}]");
    }
}

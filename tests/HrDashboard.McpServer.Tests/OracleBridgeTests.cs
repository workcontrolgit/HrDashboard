using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace HrDashboard.McpServer.Tests;

public class OracleBridgeTests
{
    [Fact]
    public void Constructor_MissingConnectionString_Throws()
    {
        var config = new ConfigurationBuilder().Build();

        var act = () => new OracleBridge(config);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ConnectionStrings:HrData*");
    }

    [Fact]
    public async Task RunSqlAsync_InvalidConnectionString_ReturnsSqlErrorMessage()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:HrData"] = "NotAValidKey=NotAValidValue"
            })
            .Build();
        var bridge = new OracleBridge(config);

        var result = await bridge.RunSqlAsync("SELECT 1 FROM dual");

        result.Should().StartWith("[SQL error:");
    }
}

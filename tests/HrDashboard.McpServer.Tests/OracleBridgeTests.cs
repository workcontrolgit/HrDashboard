using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace HrDashboard.McpServer.Tests;

public class OracleBridgeTests
{
    private static IConfiguration BuildConfig(string sqlclPath)
    {
        var values = new Dictionary<string, string?>
        {
            ["SqlclMcp:Path"]           = sqlclPath,
            ["SqlclMcp:ConnectionName"] = "hr_local"
        };
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    [Fact]
    public async Task StartAsync_WhenSqlclPathMissing_IsAvailableFalse()
    {
        var bridge = new OracleBridge(BuildConfig(string.Empty));

        await bridge.StartAsync(CancellationToken.None);

        bridge.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task RunSqlAsync_WhenNotStarted_ReturnsUnavailableMessage()
    {
        var bridge = new OracleBridge(BuildConfig(string.Empty));
        // deliberately do NOT call StartAsync

        var result = await bridge.RunSqlAsync("SELECT 1 FROM dual");

        result.Should().Be("[Oracle bridge unavailable — check SqlclMcp:Path configuration]");
    }
}

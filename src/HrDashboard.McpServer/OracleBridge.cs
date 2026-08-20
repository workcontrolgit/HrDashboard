using ModelContextProtocol.Client;
using Serilog;

namespace HrDashboard.McpServer;

/// <summary>
/// Singleton warm bridge to Oracle SQLcl MCP subprocess.
/// Started once at host startup; keeps the sql -mcp process alive.
/// </summary>
public sealed class OracleBridge : IHostedService, IAsyncDisposable, IOracleBridge
{
    private readonly string _sqlclPath;
    private readonly string _connectionName;
    private McpClient? _client;
    private bool _connected;

    public OracleBridge(IConfiguration config)
    {
        _sqlclPath     = config["SqlclMcp:Path"] ?? string.Empty;
        _connectionName = config["SqlclMcp:ConnectionName"] ?? "hr_local";
    }

    public bool IsAvailable => _client is not null;

    public async Task StartAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_sqlclPath) || !File.Exists(_sqlclPath))
        {
            Log.Warning("[OracleBridge] SQLcl not found at {Path} — Oracle tools disabled", _sqlclPath);
            return;
        }

        try
        {
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Command   = _sqlclPath,
                Arguments = ["-mcp"],
                Name      = "OracleSqlcl",
                StandardErrorLines = line => Log.Debug("[SQLcl] {Line}", line)
            });

            _client = await McpClient.CreateAsync(transport, cancellationToken: ct);
            Log.Information("[OracleBridge] Oracle SQLcl MCP started");

            await ConnectAsync(ct);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[OracleBridge] Failed to start Oracle SQLcl MCP");
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_client is not null)
            await _client.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
            await _client.DisposeAsync();
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_client is null || _connected) return;
        try
        {
            await _client.CallToolAsync("connect",
                new Dictionary<string, object?> { ["connection_name"] = _connectionName },
                cancellationToken: ct);
            _connected = true;
            Log.Information("[OracleBridge] Connected to Oracle as '{Connection}'", _connectionName);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[OracleBridge] Connect call failed — will retry on first query");
        }
    }

    public async Task<string> RunSqlAsync(string sql, CancellationToken ct = default)
    {
        if (_client is null)
            return "[Oracle bridge unavailable — check SqlclMcp:Path configuration]";

        if (!_connected)
            await ConnectAsync(ct);

        try
        {
            Log.Debug("[OracleBridge] SQL: {Sql}", sql);
            var result = await _client.CallToolAsync("sql_run",
                new Dictionary<string, object?> { ["sql"] = sql },
                cancellationToken: ct);

            var text = result.Content
                .OfType<ModelContextProtocol.Protocol.TextContentBlock>()
                .Select(c => c.Text ?? string.Empty)
                .FirstOrDefault() ?? string.Empty;

            Log.Debug("[OracleBridge] Result length: {Len}", text.Length);
            return text;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[OracleBridge] SQL execution failed");
            return $"[SQL error: {ex.Message}]";
        }
    }
}

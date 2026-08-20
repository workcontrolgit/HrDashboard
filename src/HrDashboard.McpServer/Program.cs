using HrDashboard.McpServer;
using HrDashboard.McpServer.Tools;
using Serilog;

var isStdio = args.Contains("--stdio");

var tempConfig = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var logBase = Path.Combine(SolutionRoot(), "logs", "mcpserver");

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(tempConfig)
    .WriteTo.Conditional(_ => !isStdio, wt => wt.Console())
    .WriteTo.File(
        Path.Combine(logBase, "info", "info-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7,
        restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information)
    .WriteTo.File(
        Path.Combine(logBase, "error", "error-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Error)
    .CreateLogger();

try
{
    if (isStdio)
    {
        var host = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });
        host.Services.AddSerilog();
        ConfigureServices(host.Services, host.Configuration);
        host.Services
            .AddMcpServer()
            .WithTools<HrAnalyticsTools>()
            .WithStdioServerTransport();

        using var h = host.Build();
        await h.RunAsync();
        return;
    }

    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory
    });
    builder.Host.UseSerilog();
    ConfigureServices(builder.Services, builder.Configuration);
    builder.Services
        .AddMcpServer()
        .WithTools<HrAnalyticsTools>()
        .WithHttpTransport();

    var app = builder.Build();
    app.MapMcp("/mcp");
    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "HrDashboard.McpServer terminated unexpectedly");
    Environment.ExitCode = 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

static string SolutionRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !dir.GetFiles("*.slnx").Any() && !dir.GetFiles("*.sln").Any())
        dir = dir.Parent;
    return dir?.FullName ?? AppContext.BaseDirectory;
}

static void ConfigureServices(IServiceCollection services, IConfiguration config)
{
    // OracleBridge: singleton IHostedService — starts sql -mcp subprocess at startup
    services.AddSingleton<OracleBridge>();
    services.AddHostedService(sp => sp.GetRequiredService<OracleBridge>());
}

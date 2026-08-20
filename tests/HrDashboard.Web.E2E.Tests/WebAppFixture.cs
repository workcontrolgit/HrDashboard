using System.Diagnostics;
using System.Net.Http;
using NUnit.Framework;

namespace HrDashboard.Web.E2E.Tests;

/// <summary>
/// Starts HrDashboard.Web as a real subprocess before the suite runs and stops it
/// after. Playwright drives a real browser against a real server — Blazor
/// InteractiveServer depends on a live SignalR circuit, so there is no in-memory
/// TestServer option here.
/// </summary>
[SetUpFixture]
public class WebAppFixture
{
    public const string BaseUrl = "http://localhost:5100";

    private static Process? _process;

    [OneTimeSetUp]
    public async Task StartWebAppAsync()
    {
        var repoRoot = FindRepoRoot();
        var webProjectPath = Path.Combine(repoRoot, "src", "HrDashboard.Web", "HrDashboard.Web.csproj");

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project \"{webProjectPath}\" --no-launch-profile --urls {BaseUrl}",
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.EnvironmentVariables["ASPNETCORE_ENVIRONMENT"] = "Development";

        _process = new Process { StartInfo = startInfo };
        _process.OutputDataReceived += (_, e) => { if (e.Data is not null) Console.WriteLine($"[web] {e.Data}"); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.WriteLine($"[web:err] {e.Data}"); };
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        await WaitUntilReadyAsync();
    }

    [OneTimeTearDown]
    public void StopWebApp()
    {
        if (_process is null) return;

        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }

    private static async Task WaitUntilReadyAsync()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow.AddSeconds(60);

        while (DateTime.UtcNow < deadline)
        {
            if (_process is { HasExited: true })
                throw new InvalidOperationException(
                    $"HrDashboard.Web exited early with code {_process.ExitCode}. " +
                    "Check that SQL Server LocalDB is installed and reachable.");

            try
            {
                var response = await client.GetAsync($"{BaseUrl}/login");
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException)
            {
                // Server not accepting connections yet — keep polling.
            }

            await Task.Delay(500);
        }

        throw new TimeoutException(
            $"HrDashboard.Web did not become ready at {BaseUrl} within 60 seconds. " +
            "Check that SQL Server LocalDB is installed and reachable.");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HrDashboard.slnx")))
            dir = dir.Parent;

        return dir?.FullName
            ?? throw new InvalidOperationException(
                "Could not locate HrDashboard.slnx above " + AppContext.BaseDirectory);
    }
}

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
    // NOTE: This run passes --urls http://localhost:5100 only, so Program.cs's
    // UseHttpsRedirection() is a no-op here (no HTTPS URL is configured to redirect to).
    // If that configuration ever changes to include an https:// URL, E2E navigation would
    // start getting redirected to https and fail on certificate trust.
    public const string BaseUrl = "http://localhost:5100";

    private Process? _process;

    [OneTimeSetUp]
    public async Task StartWebAppAsync()
    {
        await EnsurePortFreeAsync();

        var repoRoot = FindRepoRoot();
        var webProjectPath = Path.Combine(repoRoot, "src", "HrDashboard.Web", "HrDashboard.Web.csproj");

        RunDotnetBuildOrThrow(webProjectPath, repoRoot);

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --no-build --project \"{webProjectPath}\" --no-launch-profile --urls {BaseUrl}",
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

        try
        {
            await WaitUntilReadyAsync();
        }
        catch
        {
            // NUnit does not reliably run [OneTimeTearDown] after a failed [OneTimeSetUp],
            // so clean up the subprocess here before rethrowing the original failure.
            KillProcess();
            throw;
        }
    }

    [OneTimeTearDown]
    public void StopWebApp() => KillProcess();

    private void KillProcess()
    {
        if (_process is null) return;

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }

    private static async Task EnsurePortFreeAsync()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

        try
        {
            await client.GetAsync(BaseUrl);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // Nothing responded — the port is free. Keep going.
            return;
        }

        // Something responded (even an error response) — the point is *something* is
        // already bound to this port, so starting our own process risks the readiness
        // probe passing against a foreign server instead of ours.
        throw new InvalidOperationException(
            $"Port 5100 is already in use — something is listening at {BaseUrl}. " +
            "Stop whatever is using that port before running the E2E suite.");
    }

    private static void RunDotnetBuildOrThrow(string webProjectPath, string repoRoot)
    {
        using var buildProcess = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"build \"{webProjectPath}\"",
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("Failed to start 'dotnet build' for HrDashboard.Web.");

        buildProcess.WaitForExit();

        if (buildProcess.ExitCode != 0)
            throw new InvalidOperationException(
                $"'dotnet build' for HrDashboard.Web failed with exit code {buildProcess.ExitCode}.");
    }

    private async Task WaitUntilReadyAsync()
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
            catch (OperationCanceledException)
            {
                // A single GET attempt timed out (HttpClient.Timeout throws
                // TaskCanceledException, which derives from OperationCanceledException) —
                // keep polling instead of aborting the whole loop.
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

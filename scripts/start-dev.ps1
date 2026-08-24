<#
.SYNOPSIS
    Starts HrDashboard.McpServer and HrDashboard.Web in separate PowerShell windows.
.DESCRIPTION
    Mirrors the two-terminal "Run locally" steps in README.md: the MCP server
    is started first, then the web app, each in its own window so their
    console output stays separate and either can be stopped independently
    (Ctrl+C or closing its window).
    Both projects run through dotnet watch so supported code and content
    changes are applied without manually restarting the processes.
#>

$repoRoot   = Split-Path -Parent $PSScriptRoot
$mcpProject = Join-Path $repoRoot "src\HrDashboard.McpServer"
$webProject = Join-Path $repoRoot "src\HrDashboard.Web"

# Ports used by the two projects (HrDashboard.Web/Properties/launchSettings.json
# and HrDashboard.McpServer/appsettings.json). A leftover process from a prior
# run holding one of these blocks the new one from binding, so any process
# found listening on them is stopped before launch.
$devPorts = 5100, 7100, 5200

function Stop-DevPortProcesses {
    param([int[]]$Ports)

    $processIds = Get-NetTCPConnection -LocalPort $Ports -State Listen -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique

    foreach ($processId in $processIds) {
        $proc = Get-Process -Id $processId -ErrorAction SilentlyContinue
        if ($proc) {
            Write-Host "Stopping existing process '$($proc.ProcessName)' (PID $processId) on a dev port..." -ForegroundColor Yellow
            Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
        }
    }
}

# A spawned window inherits its parent shell's PATH as-is — it does not
# re-read the registry. If the shell you're running this script from was
# opened before the .NET SDK was installed (or before some other PATH
# update), "dotnet" would otherwise fail with "not recognized" in the new
# windows too. Refreshing PATH from the registry here fixes that regardless
# of how stale the invoking shell's own environment is.
$pathRefreshCommand = '$env:PATH = [System.Environment]::GetEnvironmentVariable(''PATH'',''Machine'') + '';'' + [System.Environment]::GetEnvironmentVariable(''PATH'',''User'')'

function Start-DevWindow {
    param(
        [string]$Title,
        [string]$ProjectPath
    )
    $command = "$pathRefreshCommand; `$host.UI.RawUI.WindowTitle = '$Title'; dotnet watch --project '$ProjectPath' run"
    Start-Process pwsh -ArgumentList @("-NoExit", "-Command", $command)
}

Stop-DevPortProcesses -Ports $devPorts

Write-Host "Starting HrDashboard.McpServer..." -ForegroundColor Cyan
Start-DevWindow -Title "HrDashboard.McpServer" -ProjectPath $mcpProject

Write-Host "Waiting a few seconds for the MCP server to start..." -ForegroundColor DarkGray
Start-Sleep -Seconds 3

Write-Host "Starting HrDashboard.Web..." -ForegroundColor Cyan
Start-DevWindow -Title "HrDashboard.Web" -ProjectPath $webProject

Write-Host ""
Write-Host "Both projects are starting in separate windows." -ForegroundColor Green
Write-Host "Check the 'HrDashboard.Web' window for the HTTPS URL to open." -ForegroundColor Green

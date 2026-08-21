<#
.SYNOPSIS
    Starts HrDashboard.McpServer and HrDashboard.Web in separate PowerShell windows.
.DESCRIPTION
    Mirrors the two-terminal "Run locally" steps in README.md: the MCP server
    is started first, then the web app, each in its own window so their
    console output stays separate and either can be stopped independently
    (Ctrl+C or closing its window).
#>

$repoRoot   = Split-Path -Parent $PSScriptRoot
$mcpProject = Join-Path $repoRoot "src\HrDashboard.McpServer"
$webProject = Join-Path $repoRoot "src\HrDashboard.Web"

Write-Host "Starting HrDashboard.McpServer..." -ForegroundColor Cyan
Start-Process pwsh -ArgumentList @(
    "-NoExit", "-Command",
    "`$host.UI.RawUI.WindowTitle = 'HrDashboard.McpServer'; dotnet run --project '$mcpProject'"
)

Write-Host "Waiting a few seconds for the MCP server to start..." -ForegroundColor DarkGray
Start-Sleep -Seconds 3

Write-Host "Starting HrDashboard.Web..." -ForegroundColor Cyan
Start-Process pwsh -ArgumentList @(
    "-NoExit", "-Command",
    "`$host.UI.RawUI.WindowTitle = 'HrDashboard.Web'; dotnet run --project '$webProject'"
)

Write-Host ""
Write-Host "Both projects are starting in separate windows." -ForegroundColor Green
Write-Host "Check the 'HrDashboard.Web' window for the HTTPS URL to open." -ForegroundColor Green

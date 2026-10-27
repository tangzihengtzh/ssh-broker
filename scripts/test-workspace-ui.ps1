param([string]$AppDirectory = 'artifacts/app', [string]$PreviewDirectory = '')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$appPath = [IO.Path]::GetFullPath((Join-Path $projectRoot $AppDirectory))
$outputPath = Join-Path $projectRoot 'artifacts\workspace-ui.json'
$profilePath = Join-Path $projectRoot ('tests\.runtime\ui-' + [guid]::NewGuid().ToString('N'))
$start = [Diagnostics.ProcessStartInfo]::new((Join-Path $appPath 'SshBroker.exe'))
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.WindowStyle = 'Hidden'
foreach ($argument in @('--pipe',('UI'+[guid]::NewGuid().ToString('N')),'--probe-workspace','true','--probe-ui','true','--probe-output',$outputPath,'--data-directory',$profilePath)) { $start.ArgumentList.Add($argument) }
if ($PreviewDirectory.Length -gt 0) { $start.ArgumentList.Add('--preview-directory'); $start.ArgumentList.Add([IO.Path]::GetFullPath((Join-Path $projectRoot $PreviewDirectory))) }
$probe = [Diagnostics.Process]::Start($start)
try {
    if (-not $probe.WaitForExit(20000)) { $probe.Kill(); throw 'Workspace UI probe timed out' }
    if ($probe.ExitCode -ne 0) { throw "Workspace UI probe exited $($probe.ExitCode)" }
    $report = Get-Content -LiteralPath $outputPath -Raw | ConvertFrom-Json
    if (-not $report.ok) { throw "Workspace UI functional check failed: $($report | ConvertTo-Json -Compress -Depth 6)" }
    $report | ConvertTo-Json -Compress -Depth 6
} finally { $probe.Dispose() }

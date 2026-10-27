param([string]$Configuration = 'Release', [switch]$SkipBuild, [switch]$Packaged)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration }
    $record = Join-Path $projectRoot 'docs\test-output.txt'
    "Tests started: $(Get-Date -Format o)" | Set-Content -LiteralPath $record -Encoding UTF8
    & dotnet test tests/SshBroker.Tests/SshBroker.Tests.csproj -c $Configuration --no-build --logger 'trx;LogFileName=unit-results.trx' --results-directory artifacts/tests 2>&1 | Tee-Object -FilePath $record -Append
    if ($LASTEXITCODE -ne 0) { throw "Unit tests failed: $LASTEXITCODE" }
    $integrationArgs = @('tests/integration.py','--configuration',$Configuration)
    if ($Packaged) { $integrationArgs += @('--app-directory','artifacts/app') }
    $integrationArgs += @('--cases','defaults_and_contract','password_cli_and_logs','telnet_shared_pty','policy_and_single_writer','gui_real_bindings','gui_owns_service')
    & python @integrationArgs 2>&1 | Tee-Object -FilePath $record -Append
    if ($LASTEXITCODE -ne 0) { throw "Integration tests failed: $LASTEXITCODE" }
    if (-not $Packaged) { throw 'Workspace functional tests require a published package; build with -Package and use -Packaged.' }
    & python tests/multi_session.py artifacts/app 2>&1 | Tee-Object -FilePath $record -Append
    if ($LASTEXITCODE -ne 0) { throw "Workspace tests failed: $LASTEXITCODE" }
    & (Join-Path $PSScriptRoot 'test-workspace-ui.ps1') | Tee-Object -FilePath $record -Append
    & python scripts/update-functional-report.py 2>&1 | Tee-Object -FilePath $record -Append
    if ($LASTEXITCODE -ne 0) { throw "Test report generation failed: $LASTEXITCODE" }
    if ($Packaged) { & (Join-Path $PSScriptRoot 'package.ps1') }
    'TEST_EXIT_CODE=0' | Tee-Object -FilePath $record -Append
} finally { Pop-Location }

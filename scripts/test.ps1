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
    & python @integrationArgs 2>&1 | Tee-Object -FilePath $record -Append
    if ($LASTEXITCODE -ne 0) { throw "Integration tests failed: $LASTEXITCODE" }
    & python scripts/update-test-report.py 2>&1 | Tee-Object -FilePath $record -Append
    if ($LASTEXITCODE -ne 0) { throw "Test report generation failed: $LASTEXITCODE" }
    if ($Packaged) { & (Join-Path $PSScriptRoot 'package.ps1') }
    'TEST_EXIT_CODE=0' | Tee-Object -FilePath $record -Append
} finally { Pop-Location }

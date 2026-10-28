param([string]$Configuration='Release', [switch]$SkipBuild, [switch]$Packaged, [string]$AppDirectory='artifacts/V2.0.1/app')
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build-release.ps1') }
    & dotnet test tests/SshBroker.Tests/SshBroker.Tests.csproj -c $Configuration --no-build --logger 'trx;LogFileName=V2.0.1-unit-results.trx' --results-directory artifacts/tests
    if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed' }
    & (Join-Path $PSScriptRoot 'test-workspace-ui.ps1') -AppDirectory $AppDirectory
    foreach ($script in @('reconnect.py','multi_session.py','tray_lifecycle.py')) {
        & python (Join-Path 'tests' $script) $AppDirectory
        if ($LASTEXITCODE -ne 0) { throw "Functional test failed: $script" }
    }
    & python tests/integration.py --app-directory $AppDirectory --cases defaults_and_contract password_cli_and_logs telnet_shared_pty policy_and_single_writer gui_real_bindings gui_owns_service slow_telnet_and_bounded_output
    if ($LASTEXITCODE -ne 0) { throw 'Compatibility tests failed' }
    & python scripts/update-functional-report.py
    if ($LASTEXITCODE -ne 0) { throw 'Report generation failed' }
    & (Join-Path $PSScriptRoot 'package.ps1') -PublishDirectory $AppDirectory -ArchiveName SshBroker-V2.0.1-win-x64.zip
} finally { Pop-Location }

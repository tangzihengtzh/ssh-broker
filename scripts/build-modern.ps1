$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    & (Join-Path $PSScriptRoot 'build.ps1')
    foreach ($project in @('SshBroker.Host','SshBroker.Cli','SshBroker.Gui')) {
        & dotnet publish "src/$project/$project.csproj" -c Release --no-restore --no-self-contained -o artifacts/app-modern --verbosity minimal
        if ($LASTEXITCODE -ne 0) { throw "Publish failed: $project" }
    }
    & (Join-Path $PSScriptRoot 'package.ps1') -PublishDirectory artifacts/app-modern -ArchiveName SshBroker-V2.0.0-modern-win-x64.zip
} finally { Pop-Location }

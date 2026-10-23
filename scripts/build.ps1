param([string]$Configuration = 'Release', [switch]$Package)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    New-Item -ItemType Directory -Path 'artifacts' -Force | Out-Null
    $record = Join-Path $projectRoot 'docs\build-output.txt'
    "Build started: $(Get-Date -Format o)" | Set-Content -LiteralPath $record -Encoding UTF8
    & dotnet restore SshBroker.sln --disable-parallel 2>&1 | Tee-Object -FilePath $record -Append
    if ($LASTEXITCODE -ne 0) { throw "Restore failed: $LASTEXITCODE" }
    & dotnet build SshBroker.sln -c $Configuration --no-restore --verbosity minimal 2>&1 | Tee-Object -FilePath $record -Append
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $LASTEXITCODE" }
    if ($Package) {
        $appDirectory = Join-Path $projectRoot 'artifacts\app'
        foreach ($project in @('SshBroker.Host','SshBroker.Cli','SshBroker.Gui')) {
            & dotnet publish "src/$project/$project.csproj" -c $Configuration --no-restore --no-self-contained -o $appDirectory 2>&1 | Tee-Object -FilePath $record -Append
            if ($LASTEXITCODE -ne 0) { throw "Publish failed for $project" }
        }
        & (Join-Path $PSScriptRoot 'package.ps1')
    }
    'BUILD_EXIT_CODE=0' | Tee-Object -FilePath $record -Append
} finally { Pop-Location }

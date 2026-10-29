param([string]$PublishDirectory = 'artifacts/app', [string]$ArchiveName = 'SshBroker-V2.0.2-win-x64.zip')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    $appDirectory = Join-Path $projectRoot $PublishDirectory
    foreach ($executable in @('SshBroker.exe','SshBroker.Host.exe','sshctl.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $appDirectory $executable))) { throw 'Publish the application before packaging.' }
    }
    Copy-Item -LiteralPath 'README.md','README.en.md','README.zh-CN.md','LICENSE','THIRD-PARTY-NOTICES.md' -Destination $appDirectory
    New-Item -ItemType Directory -Path (Join-Path $appDirectory 'docs'),(Join-Path $appDirectory 'examples') -Force | Out-Null
    foreach ($document in @('DESIGN.md','MULTI-SESSION.md','MODERN-UI.md','RECONNECT.md','USER-GUIDE.html','AUTOMATION.md','TEST-RESULTS.md','SIMPLE-LOGIN-TEST.md')) {
        $documentPath = Join-Path 'docs' $document
        if (Test-Path -LiteralPath $documentPath) { Copy-Item -LiteralPath $documentPath -Destination (Join-Path $appDirectory 'docs') }
    }
    if (Test-Path -LiteralPath 'docs/images') {
        Copy-Item -LiteralPath 'docs/images' -Destination (Join-Path $appDirectory 'docs') -Recurse -Force
    }
    Get-ChildItem -LiteralPath 'examples' -File | Copy-Item -Destination (Join-Path $appDirectory 'examples')
    $packageFiles = Get-ChildItem -LiteralPath $appDirectory | Where-Object { (-not $_.PSIsContainer -and $_.Extension -ne '.pdb') -or $_.Name -in @('docs','examples') } | Select-Object -ExpandProperty FullName
    $archive = Join-Path (Join-Path $projectRoot 'artifacts') $ArchiveName
    Compress-Archive -LiteralPath $packageFiles -DestinationPath $archive -Force
    Get-Item -LiteralPath $archive | Select-Object FullName,Length
} finally { Pop-Location }

param([Parameter(Mandatory)][string]$Pipe)
$ErrorActionPreference = 'Stop'
$cli = Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\app\sshctl.exe'
$statusJson = & $cli --pipe $Pipe status
if ($LASTEXITCODE -ne 0) { throw 'Cannot query SshBroker; start the GUI or Host first.' }
$status = $statusJson | ConvertFrom-Json
if ($status.snapshot.state -ne 'connected') { throw 'Connect and verify the SSH host fingerprint first.' }
$cursor = $status.snapshot.outputCursor
& $cli --pipe $Pipe send --client agent --text 'echo SSHBROKER_EXAMPLE_OK' --cr
if ($LASTEXITCODE -ne 0) { throw 'Send failed; inspect output before retrying.' }
& $cli --pipe $Pipe wait-output --contains SSHBROKER_EXAMPLE_OK --after $cursor --ms 5000
if ($LASTEXITCODE -ne 0) { throw 'Expected output was not observed.' }

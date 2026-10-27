# Run from the project root after publishing. Credentials are read from your environment.
$appDir = Join-Path $PWD 'artifacts\app'
Start-Process -FilePath (Join-Path $appDir 'SshBroker.Host.exe') -ArgumentList '--workspace true' -WindowStyle Hidden
$cli = Join-Path $appDir 'sshctl.exe'
$vm = (& $cli session-create --name 'Virtual machine' | ConvertFrom-Json).result
$board = (& $cli session-create --name 'Linux board' | ConvertFrom-Json).result
& $cli sessions
# Configure each target using its returned pipe. Supply your own host and trusted fingerprint.
# & $cli --pipe $vm.pipe configure --host HOST --user USER --password-env VM_PASSWORD --fingerprint SHA256:VERIFIED_KEY
# & $cli --pipe $vm.pipe connect
# & $cli --pipe $board.pipe configure --host HOST --user USER --password-env BOARD_PASSWORD --fingerprint SHA256:VERIFIED_KEY
# & $cli --pipe $board.pipe connect
# & $cli --pipe $board.pipe send --text 'pwd' --cr

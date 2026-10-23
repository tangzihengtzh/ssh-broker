# SshBroker

**English** | [简体中文](README.zh-CN.md)

Share one remote SSH terminal between people and automation agents on Windows. Connect MobaXterm through local Telnet, and use `sshctl` or the named-pipe API to control the same shell.

A standalone companion to [SerialBroker](https://github.com/tangzihengtzh/serial-broker), built with C# / .NET 8, WPF, and SSH.NET.

## Features

- One SSH PTY with shared output, working directory, environment, and terminal state.
- Local Telnet on `127.0.0.1:7001`, available only after the SSH PTY is ready.
- Shared writing by default; read-only and single-writer policies are available through the GUI dropdown or CLI.
- Password and private-key authentication. The GUI remembers approved server identities and reuses matching Windows OpenSSH trust records. Unknown servers require a first-use confirmation, without manual fingerprint entry. Changed remembered keys are rejected.
- CLI commands, line-based scripts, a REPL, JSON input/output, output watching, and a current-user named-pipe API.
- SSH disconnect, shell closure, write failure, and health timeout handling. Reconnect never automatically replays commands.
- Per-session event and raw RX/TX logs, bounded buffers, and slow-client isolation.

## Build and run

Requirements: Windows x64, .NET SDK 8.0.400 or a compatible newer 8.0.4xx SDK, and Python 3.10+ for integration tests.

```powershell
python -m pip install -r tests/requirements.txt
./scripts/build.ps1 -Package
./scripts/test.ps1 -SkipBuild -Packaged
```

The complete app is published to `artifacts/app/`. The archive is `artifacts/SshBroker-0.1.0-win-x64.zip`. Build products are not committed to Git.

| Executable | Purpose |
| --- | --- |
| `SshBroker.exe` | WPF GUI; creates a service or attaches to an existing service on the same pipe. |
| `SshBroker.Host.exe` | Headless service for scripts and agents. |
| `sshctl.exe` | Command-line client. |

The framework-dependent package requires the **.NET 8 Desktop Runtime**. Keep all companion DLL and JSON files beside the executables. Administrator privileges are not required; run the GUI and CLI as the same Windows user and privilege level.

## Quick start

1. Open `SshBroker.exe` and enter the SSH host, port, username, and password. Private-key authentication is available separately.
2. Click **Connect SSH**. Confirm the server identity on first use if it is not already trusted. Passwords remain in process memory and are never returned by the API.
3. Wait for `connected`, then create a **Telnet** session in MobaXterm to **127.0.0.1**, port **7001**. Use character mode with local line editing disabled.
4. Use `sshctl.exe` to operate the same terminal. The default control pipe is `SshBroker`.

Every client shares the same remote shell. Concurrent command input can interleave; choose the single-writer policy when exclusive input is needed.

## Headless automation

Start `SshBroker.Host.exe`, then configure and connect using the CLI. Set `SSHBROKER_PASSWORD` in the calling process. Replace the example host and fingerprint with your target and its verified host key. Headless CLI connections require an explicit pin.

```powershell
./artifacts/app/sshctl.exe configure --host 192.0.2.10 --port 22 --user user --password-env SSHBROKER_PASSWORD --fingerprint SHA256:VERIFIED_FINGERPRINT
./artifacts/app/sshctl.exe connect
./artifacts/app/sshctl.exe status
./artifacts/app/sshctl.exe send --client agent --text "pwd" --cr
./artifacts/app/sshctl.exe watch
```

Use environment variables or JSON on stdin for credentials, rather than ordinary command-line password arguments. Settings and credentials are process-local; approved GUI server identities are saved under `%LOCALAPPDATA%/SshBroker/known-hosts/`.

`sshctl --script file.sshb` executes one command per line and stops at the first error. `--script -` reads stdin; `--repl` continues interactively. Quoting, literal braces, blank lines, and comments are supported. This is a small command language, not a full Tcl interpreter; use PowerShell or Python for variables, conditions, and loops.

See the [automation reference](docs/AUTOMATION.md) and [PowerShell example](examples/session.ps1). Global options such as `--pipe NAME` must precede the command.

## GUI / CLI consistency

All mutations go through the same `CommandDispatcher` and `BrokerEngine`. Every response includes an authoritative snapshot with revision, configuration, connection state, policy, counters, log state, and errors. The GUI refreshes approximately every 250 ms.

The configuration form is an explicit draft until applied. CLI configuration changes refresh both the editor and the status display. Snapshots expose only presence flags for secrets. A successful `send` confirms SSH transport acceptance, not remote command completion; use output cursors and `wait-output` to observe results.

## Disconnects, logs, and limits

- Failures enter `faulted`, close SSH/Telnet sessions, and leave the control API available. Reconnect does not replay input.
- The default health probe executes `true` every two seconds with a four-second timeout. An empty `healthCommand` disables application-level probing.
- Each connection creates `events.jsonl`, `rx.bin`, and `tx.bin`, with a default 16 MiB limit per file. Logging failure is reported without stopping forwarding.
- Authentication arguments are excluded from event logs and snapshots. Raw terminal logs can contain sensitive session content.
- Sends are limited to 4096 bytes, with at most 128 pending writes. Revoked queued requests are not retried. Failed writes may have partially reached the server.
- Telnet binds to loopback and has no authentication. Other local processes may connect. The control pipe is restricted to the current Windows user; logical client IDs coordinate access and are not security identities.

## Tests and scope

The suite covers CLI contracts, real WPF bindings, authentication, shared PTY behavior, write policies, abrupt disconnects, stalled handshakes, network blackholes, log failures, queue revocation, and slow clients. Fixtures use isolated local ports and temporary credentials, without changing the system SSH server or firewall.

See [measured results](docs/TEST-RESULTS.md), [login verification](docs/SIMPLE-LOGIN-TEST.md), and [architecture](docs/DESIGN.md). Supplementary real Ubuntu VM testing verified password-only GUI login, character-by-character Telnet input, shared output, and compiling/running a C Hello World program.

The GUI monitor escapes control characters; MobaXterm provides full terminal rendering. SFTP/SCP, jump hosts, SSH agent integration, interactive MFA, and independent shells per downstream client are not included. Broad server compatibility, long-duration stress, and all monitor/DPI combinations remain outside the current verification scope.

## License and contributions

[MIT](LICENSE). Issues and pull requests are welcome. OpenAI Codex assisted development and is credited through Git commit co-author trailers. See [third-party notices](THIRD-PARTY-NOTICES.md).

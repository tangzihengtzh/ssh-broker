# SSH Broker V2.0.0

**English** | [简体中文](README.zh-CN.md)

A multi-device SSH workspace for Windows. Manage virtual machines and Linux boards in separate tabs, and share each remote terminal with local terminal clients and automation tools.

[Download V2.0.0](https://github.com/tangzihengtzh/ssh-broker/releases/tag/V2.0.0) · [Previous version](https://github.com/tangzihengtzh/ssh-broker/tree/main) · [Usage guide](docs/USER-GUIDE.html)

V2.0.0 is published on the **release/V2.0.0** branch and **V2.0.0** tag. The previous `main` branch and its history are preserved.

## What's new

- **Multiple SSH targets:** independent tabs, connections, Telnet ports, output, write policies and CLI endpoints. Switching or hiding the window keeps other targets running.
- **Connection history:** an editable host dropdown recalls host, SSH port, username, device name and last Telnet port. Passwords are never saved. Names may repeat and support emoji.
- **Port suggestions:** new targets avoid historical ports, other workspace targets and current listeners. Recalled and suggested values remain editable.
- **ModernWpf interface:** rounded surfaces, aligned fields and controls, with Light, Dark, Warm and Blue themes across the entire interface and monitor.
- **Persistent window layout:** window size, position, maximized state and theme are restored on startup. Historical targets are not automatically reconnected.
- **Tray operation:** Close / Alt+F4 hides the window while SSH sessions stay alive. Click the tray icon to restore; use its Exit command to quit. Active connections require exit confirmation.
- **Original app icon and separate help:** the executable, window and tray share one icon; terminal and automation instructions live in a local help document rather than instructional labels in the main interface.

## Install and start

1. Download **SshBroker-V2.0.0-win-x64.zip** from the release page and extract it into a new folder. Do not replace an older installation.
2. Install the **.NET 8 Desktop Runtime (Windows x64)** if it is not already available.
3. Run **SshBroker.exe**. Keep all DLL, JSON and documentation files beside the executables. Administrator privileges are not required.
4. Create a connection, fill in the SSH endpoint and credentials, then connect. Unknown server identities require first-use confirmation; approved identities are remembered.
5. A Telnet-capable terminal can connect to `127.0.0.1` and that target's configured local port. Use **Help** for MobaXterm / PuTTY examples and automation instructions.

The default first port is 7001 when available; subsequent suggestions avoid conflicts. Each target shares one SSH PTY between its terminal clients and CLI, including the working directory and environment. Input can interleave under the default shared-write policy; single-writer and read-only modes are also available.

To actually quit, right-click the tray icon and choose **Exit**. The icon may be in the Windows hidden-icons area. Closing an individual target tab disconnects that target; closing the whole window hides it to the tray.

## Automation

The default workspace pipe is `SshBrokerV2`. List or create a target, then use the returned `pipe` to address it independently of the selected GUI tab:

```powershell
./sshctl.exe sessions
./sshctl.exe session-create --name "Linux board"
./sshctl.exe --pipe SESSION_PIPE status
./sshctl.exe --pipe SESSION_PIPE send --text "pwd" --cr
./sshctl.exe --pipe SESSION_PIPE watch
```

Start a headless workspace with `SshBroker.Host.exe --workspace true`. CLI connection configuration uses an explicit verified host-key fingerprint, with credentials supplied through environment variables or JSON stdin. GUI and CLI mutations share the same engine and revisioned snapshots. Scripts, a REPL, output cursors and JSON responses are supported; the command language is not a full Tcl interpreter.

See [the full API](docs/AUTOMATION.md) and [workspace example](examples/workspace.ps1). Legacy single-target Host mode remains available with an explicit pipe selection.

## Settings, logs and lifecycle

- History and theme: `%LOCALAPPDATA%/SshBroker/workspace/preferences.json`.
- Window placement: `window.json` in the same directory. `--data-directory PATH` selects a separate settings directory.
- Passwords and private-key passphrases stay in memory and are not returned in API snapshots or written to history.
- Per-connection logs contain `events.jsonl`, `rx.bin` and `tx.bin`. Raw terminal logs may contain sensitive session content. The folder button opens the log root.
- One target's SSH failure does not stop other targets. Reconnection is explicit and never replays old input.
- Explicit application exit disconnects the workspace; system logoff / shutdown is not intercepted as a tray hide request.

## Build and verification

Stack: C# / .NET 8, WPF, ModernWpfUI 0.9.6 and SSH.NET 2026.0.0. Use Windows x64, .NET SDK 8.0.400 or a compatible newer 8.0.4xx SDK, and Python 3.10+ for the SSH fixtures.

```powershell
python -m pip install -r tests/requirements.txt
./scripts/build-release.ps1
```

The release directory is `artifacts/V2.0.0/app/`; the archive is `artifacts/SshBroker-V2.0.0-win-x64.zip`. Source control excludes build products, session logs, temporary test keys and machine-specific reports. Pillow is only needed to regenerate the icon assets, not to build or run the application.

Functional verification covers concurrent SSH targets, isolated Telnet routing, history and theme persistence, window restoration, GUI / CLI consistency, and continued transfer after window close. See [test results](docs/TEST-RESULTS.md) and [architecture](docs/MULTI-SESSION.md). Tests use isolated local SSH fixtures rather than changing the user's devices.

The GUI monitor escapes control characters; terminal clients handle full terminal rendering. SFTP/SCP, jump hosts, SSH agent integration, interactive MFA and separate remote shells per downstream client are outside this version's scope. Loopback Telnet has no authentication; the control pipe is limited to the current Windows user.

## License and contributors

[MIT](LICENSE). The previous license and repository history are preserved. OpenAI Codex assisted development and is credited through Git co-author trailers. See [third-party notices](THIRD-PARTY-NOTICES.md).

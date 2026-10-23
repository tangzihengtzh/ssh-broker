# Simple login verification (2026-10-23)
- Release: zero warnings/errors; 24/24 unit tests.
- Integration: 20/20; stalled upstream connection now asserts Telnet is not listening.
- Real hidden GUI, host/user/password only: connected, masked password retained, trusted identity remembered. Existing Windows OpenSSH identity matched, no manual fingerprint input.
- Real Ubuntu at the user-provided VM, localhost7001: character-by-character input, repeated commands and second-client observation all passed. Evidence: artifacts/simple-login-vm.json.
- Reported disconnections correlated with SSH timeout to a mistyped target IP; Telnet previously accepted connections before upstream readiness. No successful upstream session occurred in these logged attempts.

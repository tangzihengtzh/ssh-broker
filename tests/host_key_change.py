"""Exercise actual WPF login flow with hidden windows and isolated trust/SSH fixtures."""
import json
import os
from pathlib import Path
import subprocess
import sys
import uuid
from fixture_server import SSHFixture
root = Path(__file__).resolve().parents[1]
app = Path(sys.argv[1]).resolve()
run = root / 'tests/.runtime' / ('host-key-' + uuid.uuid4().hex)
run.mkdir(parents=True)
fixture = SSHFixture(run / 'server')
try:
    env = dict(os.environ, SSHBROKER_PROBE_PORT=str(fixture.port),
        SSHBROKER_PROBE_PASSWORD=fixture.password, SSHBROKER_PROBE_FINGERPRINT=fixture.fingerprint)
    report = run / 'gui.json'
    result = subprocess.run([str(app / 'SshBroker.exe'), '--pipe', 'HostKeyProbe.' + uuid.uuid4().hex,
        '--probe-output', str(report), '--probe-own', 'true', '--probe-host-key', 'true'],
        env=env, capture_output=True, timeout=45, creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
    data = json.loads(report.read_text(encoding='utf-8'))
    print(json.dumps(data, ensure_ascii=False), flush=True)
    assert data['ok'], 'WPF host key confirmation flow failed'
    assert fixture.auth_attempts == 2, ('Unexpected authentication attempts', fixture.auth_attempts)
    assert fixture.shells == 2, ('Unexpected shells', fixture.shells)
    logroot = run / 'logs'
    events = [json.loads(line) for path in logroot.rglob('events.jsonl') for line in path.read_text().splitlines() if line]
    mismatch = [e for e in events if e['kind'] == 'host_key_mismatch' and fixture.fingerprint in e['message']]
    assert mismatch and 'expected=SHA256:previous-board-installation' in mismatch[-1]['message'], 'Missing fingerprint diagnostics'
    (root / 'artifacts/host-key-results.json').write_text(json.dumps(dict(data, testedBinaryDirectory=str(app), runtimeDirectory=str(run)), indent=2), encoding='utf-8')
    print('PASS isolated WPF host key change / cancel / accept / persisted reconnect / password auth')
finally:
    fixture.close()

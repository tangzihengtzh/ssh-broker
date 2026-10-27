"""Real WPF close cancellation with two active SSH/Telnet targets and explicit exit."""
import json
import subprocess
import sys
import telnetlib
import time
import uuid
from pathlib import Path
from fixture_server import SSHFixture

root = Path(__file__).resolve().parents[1]
app = Path(sys.argv[1]).resolve()
run = root / 'tests/.runtime' / ('tray-' + uuid.uuid4().hex)
run.mkdir(parents=True)
pipe = 'TrayTest.' + uuid.uuid4().hex
report = run / 'gui.json'
fixtures = [SSHFixture(run / 'a'), SSHFixture(run / 'b')]
hidden = getattr(subprocess, 'CREATE_NO_WINDOW', 0)
gui = subprocess.Popen([str(app / 'SshBroker.exe'), '--pipe', pipe, '--probe-workspace', 'true',
                        '--probe-tray', 'true', '--probe-seconds', '45', '--probe-output', str(report),
                        '--data-directory', str(run / 'preferences')], creationflags=hidden)
def rpc(target, method, params=None):
    p = subprocess.run([str(app / 'sshctl.exe'), '--pipe', target, '--json'],
        input=json.dumps(dict(id=uuid.uuid4().hex, method=method, params=params or {})),
        capture_output=True, text=True, encoding='utf-8', timeout=10, creationflags=hidden)
    response = json.loads(p.stdout)
    assert response['ok'], response['code']
    return response
def until(condition):
    deadline = time.monotonic() + 12
    while time.monotonic() < deadline:
        try:
            value = condition()
            if value: return value
        except (OSError, ValueError): pass
        time.sleep(.15)
    raise AssertionError('Tray functional wait timed out')
success = False
try:
    until(lambda: report.exists())
    sessions = [rpc(pipe, 'session-create')['result'] for _ in range(2)]
    for s,f in zip(sessions, fixtures):
        rpc(s['pipe'], 'configure', dict(host='127.0.0.1', port=f.port, username=f.username,
            password=f.password, hostKeyFingerprint=f.fingerprint, logRoot=str(run / s['id'])))
        rpc(s['pipe'], 'connect')
    until(lambda: json.loads(report.read_text())['inBackground'])
    assert gui.poll() is None
    print('PASS real window Close is cancelled while both SSH targets remain active', flush=True)
    for index,s in enumerate(sessions):
        state = rpc(s['pipe'], 'status')['snapshot']
        assert state['state'] == 'connected' and state['telnetListening']
        with telnetlib.Telnet('127.0.0.1',state['localTelnetPort'],timeout=5) as terminal:
            token = ('TRAY_ALIVE_' + str(index)).encode()
            terminal.write(b'echo ' + token + b'\r')
            assert token in terminal.read_until(token,timeout=5)
    print('PASS both Telnet streams still accept and return data after window close', flush=True)
    rpc(pipe, 'shutdown')
    gui.wait(timeout=15)
    assert gui.returncode == 0
    print('PASS explicit shutdown exits GUI and disposes tray/runtime', flush=True)
    success = True
finally:
    if gui.poll() is None: gui.terminate(); gui.wait(timeout=5)
    for f in fixtures: f.close()
    (root / 'artifacts/tray-results.json').write_text(json.dumps(dict(ok=success, runtimeDirectory=str(run))),encoding='utf-8')

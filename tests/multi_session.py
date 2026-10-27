"""Focused workspace functionality: two SSH targets, Telnet routing, GUI, history and restart."""
import json
import os
import subprocess
import sys
import telnetlib
import time
import uuid
from pathlib import Path
from fixture_server import SSHFixture

ROOT = Path(__file__).resolve().parents[1]
APP = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else ROOT / 'artifacts/app'
RUN = ROOT / 'tests/.runtime' / ('multi-' + uuid.uuid4().hex)
RUN.mkdir(parents=True)
PIPE = 'MultiTest.' + uuid.uuid4().hex
HIDDEN = getattr(subprocess, 'CREATE_NO_WINDOW', 0)
checks = []
fixtures = [SSHFixture(RUN / 'keys-a'), SSHFixture(RUN / 'keys-b')]
children = []


def check(name, condition):
    assert condition, name
    checks.append(name)
    print('PASS ' + name, flush=True)


def rpc(pipe, method, params=None, ok=True):
    p = subprocess.run([str(APP / 'sshctl.exe'), '--pipe', pipe, '--json'],
                       input=json.dumps(dict(id=uuid.uuid4().hex, method=method, params=params or {})),
                       capture_output=True, text=True, encoding='utf-8', timeout=20, creationflags=HIDDEN)
    data = json.loads(p.stdout)
    assert data['ok'] == ok, (method, data['code'])
    return data


def until(predicate, seconds=8):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        try:
            value = predicate()
            if value:
                return value
        except (OSError, ValueError, subprocess.TimeoutExpired):
            pass
        time.sleep(.15)
    raise AssertionError('Timed out waiting for functional result')


def launch():
    p = subprocess.Popen([str(APP / 'SshBroker.Host.exe'), '--workspace', 'true', '--pipe', PIPE,
                          '--data-directory', str(RUN / 'preferences')],
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=HIDDEN)
    children.append(p)
    until(lambda: rpc(PIPE, 'sessions')['ok'])
    return p


try:
    host = launch()
    check('empty workspace starts without connections', rpc(PIPE, 'sessions')['result'] == [])
    sessions = [rpc(PIPE, 'session-create', {'name': '开发设备'})['result'] for _ in range(2)]
    ports = [s['snapshot']['config']['telnetPort'] for s in sessions]
    check('new session ports differ and duplicate names are allowed', ports[0] != ports[1] and sessions[0]['name'] == sessions[1]['name'])
    for index, (session, fixture) in enumerate(zip(sessions, fixtures)):
        rpc(session['pipe'], 'configure', dict(host='127.0.0.1', port=fixture.port, username=fixture.username,
            password=fixture.password, hostKeyFingerprint=fixture.fingerprint, logRoot=str(RUN / ('logs-' + str(index))), keepAliveSeconds=1, healthTimeoutMs=900))
        check('SSH target ' + str(index + 1) + ' connects', rpc(session['pipe'], 'connect')['snapshot']['state'] == 'connected')
    report = RUN / 'workspace-projection.json'
    gui = subprocess.Popen([str(APP / 'SshBroker.exe'), '--pipe', PIPE, '--probe-workspace', 'true',
                            '--probe-output', str(report), '--probe-seconds', '40'],
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=HIDDEN)
    children.append(gui)
    until(lambda: report.exists() and len(json.loads(report.read_text())['tabs']) == 2)
    with telnetlib.Telnet('127.0.0.1', ports[0], timeout=5) as a, telnetlib.Telnet('127.0.0.1', ports[1], timeout=5) as b:
        a.write(b'echo TARGET_A\r'); b.write(b'echo TARGET_B\r')
        out_a = a.read_until(b'TARGET_A', timeout=5)
        out_b = b.read_until(b'TARGET_B', timeout=5)
        check('Telnet routes to independent targets', b'TARGET_A' in out_a and b'TARGET_B' not in out_a and b'TARGET_B' in out_b and b'TARGET_A' not in out_b)
        rpc(sessions[1]['pipe'], 'send', {'text': 'echo CLI_B\r', 'client': 'test'})
        check('CLI shares the corresponding Telnet PTY', b'CLI_B' in b.read_until(b'CLI_B', timeout=5))
        def bindings():
            tabs = json.loads(report.read_text())['tabs']
            return len(tabs) == 2 and all(t['state'] == 'connected' and t['binding']['ok'] and t['rx'] > 0 for t in tabs)
        check('both GUI tab bindings match CLI authoritative snapshots', until(bindings))
        fixtures[0].drop_all()
        until(lambda: rpc(sessions[0]['pipe'], 'status')['snapshot']['state'] == 'faulted')
        b.write(b'echo SURVIVES_A_DROP\r')
        check('one SSH drop leaves the other target usable', b'SURVIVES_A_DROP' in b.read_until(b'SURVIVES_A_DROP', timeout=5))
        rpc(PIPE, 'session-close', {'id': sessions[0]['id']})
        check('closing one tab preserves the other session', len(rpc(PIPE, 'sessions')['result']) == 1 and rpc(sessions[1]['pipe'], 'status')['snapshot']['state'] == 'connected')
    history = rpc(PIPE, 'history')['result']['preferences']['history']
    check('history stores both SSH endpoints and their Telnet ports', len(history) == 2 and {p['telnetPort'] for p in history} == set(ports))
    raw = (RUN / 'preferences/preferences.json').read_text()
    check('history contains no passwords or passphrases', all(f.password not in raw for f in fixtures) and 'password' not in raw.lower() and 'passphrase' not in raw.lower())
    rpc(PIPE, 'palette', {'value': 'Light'})
    until(lambda: json.loads(report.read_text())['palette'] == 'Light')
    check('CLI palette changes synchronize to GUI', True)
    gui.terminate(); gui.wait(timeout=5)
    rpc(PIPE, 'shutdown'); host.wait(timeout=12)
    host = launch()
    prefs = rpc(PIPE, 'history')['result']['preferences']
    check('restart restores history and palette without reconnecting', len(prefs['history']) == 2 and prefs['palette'] == 'Light' and rpc(PIPE, 'sessions')['result'] == [])
    rpc(PIPE, 'shutdown'); host.wait(timeout=12)
finally:
    for child in children:
        if child.poll() is None:
            child.terminate(); child.wait(timeout=8)
    for fixture in fixtures:
        fixture.close()
    result = dict(passed=len(checks), checks=checks, runtimeDirectory=str(RUN))
    (ROOT / 'artifacts/multi-session-results.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
print(json.dumps(dict(passed=len(checks))), flush=True)

"""Capture real WPF views with isolated SSH demo data, without showing a foreground window."""
import json
import subprocess
import sys
import time
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tests'))
from fixture_server import SSHFixture

APP = Path(sys.argv[1]).resolve()
RUN = ROOT / 'tests/.runtime' / ('screenshots-' + uuid.uuid4().hex)
RUN.mkdir(parents=True)
OUT = ROOT / 'artifacts/product-screenshots'
OUT.mkdir(exist_ok=True)
for name in ['welcome.png', 'workspace.png', 'capture.ready']:
    (OUT / name).unlink(missing_ok=True)
PIPE = 'ProductShot.' + uuid.uuid4().hex
HIDDEN = getattr(subprocess, 'CREATE_NO_WINDOW', 0)
fixtures = [SSHFixture(RUN / 'a'), SSHFixture(RUN / 'b')]
report = RUN / 'gui.json'
gui = subprocess.Popen([str(APP / 'SshBroker.exe'), '--pipe', PIPE, '--probe-workspace', 'true',
    '--probe-output', str(report), '--probe-seconds', '35', '--data-directory', str(RUN / 'settings'),
    '--screenshot-directory', str(OUT)], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=HIDDEN)
def rpc(pipe, method, params=None):
    p = subprocess.run([str(APP / 'sshctl.exe'), '--pipe', pipe, '--json'],
        input=json.dumps(dict(id=uuid.uuid4().hex,method=method,params=params or {})),
        capture_output=True,text=True,encoding='utf-8',timeout=10,creationflags=HIDDEN)
    result=json.loads(p.stdout)
    assert result['ok'],result['code']
    return result
def until(condition):
    end=time.monotonic()+15
    while time.monotonic()<end:
        try:
            if condition():return
        except (OSError,ValueError):pass
        time.sleep(.15)
    raise RuntimeError('Screenshot wait timed out')
try:
    until(lambda:(OUT/'welcome.png').exists())
    for name,fixture in zip(['开发环境 A','开发板 B'],fixtures):
        s=rpc(PIPE,'session-create',{'name':name})['result']
        rpc(s['pipe'],'configure',dict(host='127.0.0.1',port=fixture.port,username=fixture.username,
            password=fixture.password,hostKeyFingerprint=fixture.fingerprint,logRoot=str(RUN/'logs')))
        rpc(s['pipe'],'connect')
        for text in ['Shared SSH terminal ready.','Automatic reconnect: 3 attempts / 2 seconds.','Session output remains visible after recovery.']:
            rpc(s['pipe'],'send',{'text':'echo '+text+'\r'})
    until(lambda:report.exists() and 'Session output remains visible' in report.read_text())
    (OUT/'capture.ready').write_text('Capture isolated demo workspace.')
    until(lambda:(OUT/'workspace.png').exists())
    rpc(PIPE,'shutdown');gui.wait(timeout=12)
    print('Captured welcome and connected workspace from actual WPF controls with isolated SSH fixtures.')
finally:
    if gui.poll() is None:gui.terminate();gui.wait(timeout=5)
    for fixture in fixtures:fixture.close()

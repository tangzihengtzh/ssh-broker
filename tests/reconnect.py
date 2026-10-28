"""Isolated retry/replay/cancellation tests. Never attaches to a user's running workspace."""
import json
import socket
import subprocess
import sys
import telnetlib
import time
import uuid
from pathlib import Path
from fixture_server import SSHFixture

ROOT = Path(__file__).resolve().parents[1]
APP = Path(sys.argv[1]).resolve()
RUN = ROOT / 'tests/.runtime' / ('reconnect-' + uuid.uuid4().hex)
RUN.mkdir(parents=True)
PIPE = 'ReconnectTest.' + uuid.uuid4().hex
HIDDEN = getattr(subprocess, 'CREATE_NO_WINDOW', 0)
fixtures = [SSHFixture(RUN / 'a'), SSHFixture(RUN / 'b')]
checks = []
children = []
def rpc(pipe, method, params=None, ok=True):
    p = subprocess.run([str(APP / 'sshctl.exe'), '--pipe', pipe, '--json'],
        input=json.dumps(dict(id=uuid.uuid4().hex, method=method, params=params or {})),
        capture_output=True, text=True, encoding='utf-8', timeout=15, creationflags=HIDDEN)
    result = json.loads(p.stdout)
    assert result['ok'] == ok, (method,result['code'])
    return result
def until(condition, seconds=10):
    deadline=time.monotonic()+seconds
    while time.monotonic()<deadline:
        try:
            value=condition()
            if value: return value
        except (OSError,ValueError,subprocess.TimeoutExpired): pass
        time.sleep(.08)
    raise AssertionError('Timed out waiting for recovery condition')
def check(name,condition=True):
    assert condition,name
    checks.append(name);print('PASS '+name,flush=True)
def state(s): return rpc(s['pipe'],'status')['snapshot']
def connect(s,f,**changes):
    rpc(s['pipe'],'disconnect')
    values=dict(host='127.0.0.1',port=f.port,username=f.username,password=f.password,
        hostKeyFingerprint=f.fingerprint,connectTimeoutMs=650,operationTimeoutMs=1000,
        keepAliveSeconds=1,healthTimeoutMs=900,reconnectAttempts=3,reconnectDelayMs=450,
        logRoot=str(RUN / ('logs-'+s['id'])))
    values.update(changes);rpc(s['pipe'],'configure',values)
    return rpc(s['pipe'],'connect')['snapshot']
def events(s):
    found=[]
    for p in (RUN / ('logs-'+s['id'])).rglob('events.jsonl'):
        found.extend(json.loads(line) for line in p.read_text().splitlines() if line)
    return found

success=False
host=subprocess.Popen([str(APP/'SshBroker.Host.exe'),'--workspace','true','--pipe',PIPE,'--data-directory',str(RUN/'settings')],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,creationflags=HIDDEN)
children.append(host)
try:
    until(lambda:rpc(PIPE,'sessions')['ok'])
    a,b=[rpc(PIPE,'session-create',{'name':name})['result'] for name in ['test-a','test-b']]
    first=connect(a,fixtures[0]);second=connect(b,fixtures[1],reconnectAttempts=0)
    port=first['localTelnetPort']
    gui_report=RUN/'gui.json'
    gui=subprocess.Popen([str(APP/'SshBroker.exe'),'--pipe',PIPE,'--probe-workspace','true','--probe-output',str(gui_report),'--probe-seconds','90'],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,creationflags=HIDDEN)
    children.append(gui)
    with telnetlib.Telnet('127.0.0.1',port,timeout=4) as terminal:
        terminal.write(b'echo BEFORE_RECONNECT\r')
        assert b'BEFORE_RECONNECT' in terminal.read_until(b'BEFORE_RECONNECT',timeout=4)
        until(lambda: gui_report.exists() and 'BEFORE_RECONNECT' in gui_report.read_text())
        fixtures[0].drop_all()
        until(lambda:state(a)['state']=='reconnecting')
        terminal.write(b'echo MUST_NOT_REPLAY\r')
        denied=rpc(a['pipe'],'send',{'text':'echo ALSO_NO_REPLAY\r'},ok=False)
        assert denied['code']=='not_connected'
        until(lambda:state(a)['state']=='connected' and state(a)['generation']>first['generation'])
        reply=terminal.read_until(b'SSH reconnected.',timeout=5)
        check('original Telnet socket survives automatic SSH recovery',b'SSH reconnected.' in reply)
        terminal.write(b'echo AFTER_RECONNECT\r')
        check('original shared terminal accepts new input after recovery',b'AFTER_RECONNECT' in terminal.read_until(b'AFTER_RECONNECT',timeout=4))
        with telnetlib.Telnet('127.0.0.1',port,timeout=4) as late:
            cache=late.read_until(b'AFTER_RECONNECT',timeout=4)
            check('late Telnet client receives pre-disconnect history and recovery boundary',b'BEFORE_RECONNECT' in cache and b'SSH reconnected.' in cache)
        check('offline input and earlier commands are never replayed',b'MUST_NOT_REPLAY' not in fixtures[0].received and b'ALSO_NO_REPLAY' not in fixtures[0].received and fixtures[0].received.count(b'echo BEFORE_RECONNECT')==1)
        until(lambda:'AFTER_RECONNECT' in gui_report.read_text())
        check('GUI retains old output and shows new output without cursor reset','BEFORE_RECONNECT' in gui_report.read_text())
    restored=state(a)
    check('cause and attempt telemetry survive successful recovery',restored['lastDisconnectAt'] and restored['lastDisconnect']['code'] and restored['reconnectSuccesses']>=1)
    before=state(b)['outputCursor']
    rpc(b['pipe'],'send',{'text':'echo OTHER_TARGET_ALIVE\r'})
    rpc(b['pipe'],'wait-output',{'contains':'OTHER_TARGET_ALIVE','after':before,'ms':3000})
    check('other target is unaffected by recovery')
    kinds={e['kind'] for e in events(a)}
    check('diagnostic and attempt logs are recorded',{'disconnect_diagnostic','reconnect_scheduled','reconnect_attempt','reconnect_success','offline_input_dropped'}.issubset(kinds))

    connect(a,fixtures[0],healthTimeoutMs=250)
    fixtures[0].health_silent.set()
    until(lambda:state(a)['state']=='reconnecting')
    fixtures[0].health_silent.clear()
    until(lambda:state(a)['state']=='connected' and state(a)['reconnectSuccesses']>restored['reconnectSuccesses'])
    check('health-probe timeout recovers and retains its diagnostic cause',state(a)['lastDisconnect']['code']=='health_timeout')

    connect(a,fixtures[0],reconnectDelayMs=2000)
    fixtures[0].drop_all();until(lambda:state(a)['state']=='reconnecting')
    generation=state(a)['generation'];count=fixtures[0].connections
    rpc(a['pipe'],'disconnect');time.sleep(2.2)
    check('explicit disconnect cancels delayed retries',state(a)['state']=='disconnected' and state(a)['generation']==generation and fixtures[0].connections==count)

    connect(a,fixtures[0])
    original_password=fixtures[0].password
    fixtures[0].password='changed-'+uuid.uuid4().hex
    fixtures[0].drop_all()
    until(lambda:state(a)['state']=='faulted' and not state(a)['autoReconnectActive'])
    failed=state(a);count=fixtures[0].connections;time.sleep(.8)
    check('authentication failure stops recovery immediately',failed['lastError']['code']=='authentication_failed' and failed['reconnectAttempt']==1 and fixtures[0].connections==count)
    fixtures[0].password=original_password

    connected=connect(a,fixtures[0],reconnectAttempts=2,reconnectDelayMs=200)
    original_session=fixtures[0]._session
    def reject(connection):
        with fixtures[0].lock: fixtures[0].connections+=1
        connection.close()
    fixtures[0]._session=reject
    before_connections=fixtures[0].connections
    fixtures[0].drop_all()
    until(lambda:state(a)['state']=='faulted' and not state(a)['autoReconnectActive'],12)
    failed=state(a);time.sleep(.7)
    check('transient connection failures stop at the configured retry limit',failed['reconnectAttempt']==2 and fixtures[0].connections-before_connections==2 and not failed['telnetListening'])
    fixtures[0]._session=original_session
    rpc(a['pipe'],'connect')
    check('manual connection works after exhaustion',state(a)['state']=='connected')

    connect(a,fixtures[0],reconnectAttempts=2,reconnectDelayMs=200)
    for number in range(2):
        generation=state(a)['generation'];fixtures[0].drop_all()
        until(lambda:state(a)['state']=='connected' and state(a)['generation']>generation)
    fixtures[0].drop_all()
    until(lambda:state(a)['state']=='faulted' and not state(a)['autoReconnectActive'])
    check('rapidly flapping links share the retry budget',state(a)['reconnectAttempt']==2)

    connect(a,fixtures[0],reconnectDelayMs=200,connectTimeoutMs=8000)
    original_session=fixtures[0]._session
    stalled=[]
    def stall(connection):
        stalled.append(connection)
    fixtures[0]._session=stall
    fixtures[0].drop_all()
    until(lambda:len(stalled)>0)
    started=time.monotonic();rpc(a['pipe'],'disconnect')
    check('explicit disconnect also cancels an in-progress retry handshake',time.monotonic()-started<3 and state(a)['state']=='disconnected')
    for connection in stalled: connection.close()
    fixtures[0]._session=original_session

    connect(a,fixtures[0],reconnectDelayMs=2000)
    fixtures[0].drop_all();until(lambda:state(a)['state']=='reconnecting')
    count=fixtures[0].connections;rpc(PIPE,'session-close',{'id':a['id']});time.sleep(2.2)
    check('closing a target cancels recovery without disturbing other targets',fixtures[0].connections==count and state(b)['state']=='connected')
    rpc(PIPE,'shutdown');host.wait(timeout=12)
    check('workspace shuts down cleanly after retry tests',host.returncode==0)
    success=True
finally:
    for child in children:
        if child.poll() is None: child.terminate();child.wait(timeout=6)
    for f in fixtures:f.close()
    (ROOT/'artifacts/reconnect-results.json').write_text(json.dumps(dict(ok=success,passed=len(checks),checks=checks,runtimeDirectory=str(RUN)),indent=2),encoding='utf-8')

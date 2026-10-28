from __future__ import annotations
import argparse
import base64
import json
import os
import socket
import subprocess
import threading
import time
import traceback
import uuid
from pathlib import Path
from fixture_server import SSHFixture, BlackholeProxy, SilentListener

ROOT = Path(__file__).resolve().parents[1]
FRAMEWORK = "net8.0-windows"
CREATE_HIDDEN = getattr(subprocess, "CREATE_NO_WINDOW", 0)


class Suite:
    def __init__(self, config="Release", app_directory=None):
        self.run_root = ROOT / "tests" / ".runtime" / uuid.uuid4().hex
        self.run_root.mkdir(parents=True)
        self.fixture = SSHFixture(self.run_root / "keys")
        self.pipe = "SshBroker.test." + uuid.uuid4().hex
        self.env = dict(os.environ, SSHBROKER_TEST_PASSWORD=self.fixture.password,
                        SSHBROKER_TEST_KEYPASS=self.fixture.passphrase, SSHBROKER_BAD_PASSWORD="incorrect-" + uuid.uuid4().hex)
        self.host_exe = ROOT / "src" / "SshBroker.Host" / "bin" / config / FRAMEWORK / "SshBroker.Host.exe"
        self.cli_exe = ROOT / "src" / "SshBroker.Cli" / "bin" / config / FRAMEWORK / "sshctl.exe"
        self.gui_exe = ROOT / "src" / "SshBroker.Gui" / "bin" / config / FRAMEWORK / "SshBroker.exe"
        self.binary_directory = str(self.host_exe.parent)
        if app_directory:
            directory = Path(app_directory).resolve()
            self.host_exe = directory / "SshBroker.Host.exe"
            self.cli_exe = directory / "sshctl.exe"
            self.gui_exe = directory / "SshBroker.exe"
            self.binary_directory = str(directory)
        self.host_log = (self.run_root / "host-output.txt").open("w", encoding="utf-8")
        self.host = subprocess.Popen([str(self.host_exe), "--pipe", self.pipe, "--log-root", str(self.run_root / "logs")],
                                     stdout=self.host_log, stderr=subprocess.STDOUT, creationflags=CREATE_HIDDEN)
        self.processes = []
        self.results = []
        self.wait_status(lambda s: s["state"] == "disconnected", 10)

    def redact(self, text):
        return text.replace(self.fixture.password, "[REDACTED]").replace(self.fixture.passphrase, "[REDACTED]")

    def cli_process(self, *args, stdout=subprocess.PIPE):
        process = subprocess.Popen([str(self.cli_exe), "--pipe", self.pipe, *map(str, args)],
                                   stdin=subprocess.PIPE, stdout=stdout, stderr=subprocess.PIPE, env=self.env, creationflags=CREATE_HIDDEN)
        self.processes.append(process)
        return process

    def cli(self, *args, ok=True, timeout=20, input_text=None):
        process = self.cli_process(*args)
        out, err = process.communicate(None if input_text is None else input_text.encode(), timeout=timeout)
        stdout, stderr = out.decode("utf-8", "replace"), err.decode("utf-8", "replace")
        lines = [line for line in stdout.splitlines() if line.strip()]
        response = json.loads(lines[-1]) if lines else json.loads(stderr.splitlines()[-1])
        if ok is True and (process.returncode != 0 or not response.get("ok", False)):
            raise AssertionError(self.redact(f"CLI failed {args[0]}: rc={process.returncode} {response}"))
        if ok is False and (process.returncode == 0 or response.get("ok", True)):
            raise AssertionError(f"Expected CLI failure for {args[0]}")
        return response

    def raw(self, method, params=None, ok=True):
        return self.cli("--json", input_text=json.dumps({"id": uuid.uuid4().hex, "method": method, "params": params or {}}), ok=ok)

    def status(self):
        return self.cli("status")["snapshot"]

    def wait_status(self, predicate, seconds=6):
        deadline = time.monotonic() + seconds
        last = None
        while time.monotonic() < deadline:
            try:
                last = self.status()
                if predicate(last):
                    return last
            except (AssertionError, json.JSONDecodeError, subprocess.TimeoutExpired):
                pass
            time.sleep(0.05)
        raise AssertionError(self.redact(f"Status condition not reached: {last}"))

    def configure(self, **overrides):
        if self.status()["state"] != "disconnected":
            self.cli("disconnect")
        values = dict(host="127.0.0.1", port=self.fixture.port, username=self.fixture.username,
                      authentication="password", password=self.fixture.password, privateKeyPath="",
                      keyPassphrase="", hostKeyFingerprint=self.fixture.fingerprint,
                      telnetEnabled=True, telnetPort=0, columns=120, rows=32,
                      connectTimeoutMs=1500, operationTimeoutMs=1800, keepAliveSeconds=1,
                      healthTimeoutMs=900, healthCommand="true", reconnectAttempts=0, logRoot=str(self.run_root / "logs"),
                      logLimitBytes=16 * 1024 * 1024)
        values.update(overrides)
        self.raw("configure", values)
        self.cli("policy", "shared")

    def connect(self, **overrides):
        self.configure(**overrides)
        return self.cli("connect")["snapshot"]

    def send_echo(self, marker=None, client="agent"):
        marker = marker or "OK-" + uuid.uuid4().hex
        cursor = self.status()["outputCursor"]
        self.cli("send", "--text", "echo " + marker, "--cr", "--client", client)
        self.cli("wait-output", "--contains", marker, "--after", cursor, "--ms", 5000)
        return marker

    def case(self, name, function):
        start = time.monotonic()
        try:
            function()
            self.results.append(dict(name=name, ok=True, seconds=round(time.monotonic() - start, 3)))
            print("PASS " + name, flush=True)
        except Exception as error:
            self.results.append(dict(name=name, ok=False, seconds=round(time.monotonic() - start, 3),
                                     error=self.redact(str(error)), trace=self.redact(traceback.format_exc())))
            print("FAIL " + name + ": " + self.redact(str(error)), flush=True)

    def close(self):
        for process in self.processes:
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=4)
                except subprocess.TimeoutExpired:
                    process.kill()
        if self.host.poll() is None:
            try:
                self.cli("shutdown", timeout=5)
                self.host.wait(timeout=10)
            except Exception:
                self.host.kill()
                self.host.wait(timeout=4)
        self.host_log.close()
        self.fixture.close()

    def test_defaults_and_contract(self):
        snapshot = self.status()
        assert snapshot["policy"] == "shared"
        response = self.cli("projection")
        projection, authoritative = response["result"], response["snapshot"]
        for field in ("revision", "state", "policy", "rxBytes", "txBytes"):
            assert projection[field] == authoritative[field], field
        help_process = self.cli_process("--help")
        out, _ = help_process.communicate(timeout=5)
        assert help_process.returncode == 0 and b"--script" in out
        assert self.raw("missing-command", ok=False)["code"] == "unknown_command"
        assert self.cli("--json", input_text="{invalid", ok=False)["code"] == "invalid_request"

    def test_config_atomicity(self):
        before = self.status()
        assert self.raw("configure", {"port": 99999}, ok=False)["code"] == "invalid_config"
        assert self.status()["config"] == before["config"]
        assert self.raw("configure", {"host": "localhost", "expectedRevision": -1}, ok=False)["code"] == "revision_conflict"
        assert self.raw("configure", {"unexpected": 1}, ok=False)["code"] == "invalid_params"

    def test_host_key_and_auth(self):
        self.configure(hostKeyFingerprint="")
        response = self.cli("connect", ok=False)
        assert response["code"] == "host_key_unknown" and response["snapshot"]["observedHostKey"] == self.fixture.fingerprint
        assert not self.wait_status(lambda s: not s["telnetListening"])["clients"]
        self.configure(hostKeyFingerprint="SHA256:wrong")
        assert self.cli("connect", ok=False)["code"] == "host_key_mismatch"
        self.configure(password=self.env["SSHBROKER_BAD_PASSWORD"])
        assert self.cli("connect", ok=False)["code"] == "authentication_failed"

    def test_password_cli_and_logs(self):
        snapshot = self.connect()
        assert snapshot["state"] == "connected" and snapshot["hasPassword"]
        assert "password" not in snapshot["config"]
        assert self.fixture.password not in json.dumps(snapshot)
        marker = self.send_echo()
        self.send_echo("Unicode-自动化接口-" + uuid.uuid4().hex)
        self.cli("send", "--text", "--pipe", "--cr")
        self.cli("send", "--hex", "03")
        self.cli("resize", "--columns", 144, "--rows", 44)
        assert self.status()["config"]["columns"] == 144
        assert (144, 44) in self.fixture.sizes
        folder = Path(self.status()["logDirectory"])
        assert marker.encode() in (folder / "rx.bin").read_bytes()
        assert marker.encode() in (folder / "tx.bin").read_bytes()
        events = [json.loads(line) for line in (folder / "events.jsonl").read_text().splitlines()]
        assert any(e["kind"] == "connected" for e in events)
        assert self.raw("configure", {"port": 22}, ok=False)["code"] == "busy"

    def test_telnet_shared_pty(self):
        snapshot = self.connect()
        shells = self.fixture.shells
        peer = socket.create_connection(("127.0.0.1", snapshot["localTelnetPort"]), timeout=3)
        peer.settimeout(0.2)
        try:
            self.wait_status(lambda s: any(c["kind"] == "telnet" for c in s["clients"]))
            marker = "TELNET-" + uuid.uuid4().hex
            peer.sendall(("echo " + marker + "\r\0").encode())
            self.cli("wait-output", "--contains", marker, "--after", snapshot["outputCursor"], "--ms", 5000)
            other = self.send_echo()
            received = bytearray()
            end = time.monotonic() + 4
            while other.encode() not in received and time.monotonic() < end:
                try:
                    received.extend(peer.recv(65536))
                except socket.timeout:
                    pass
            assert marker.encode() in received and other.encode() in received
            assert self.fixture.shells == shells, "Clients opened extra remote shells"
        finally:
            peer.close()
        self.wait_status(lambda s: not any(c["kind"] == "telnet" for c in s["clients"]))

    def test_policy_and_single_writer(self):
        self.connect()
        self.cli("policy", "readonly")
        assert self.cli("send", "--text", "must-not-send", ok=False)["code"] == "write_denied"
        assert self.cli("acquire", ok=False)["code"] == "write_denied"
        self.cli("policy", "single")
        self.send_echo(client="first")
        assert self.status()["writer"] == "first"
        self.cli("policy", "single")
        assert self.status()["writer"] == "first", "Duplicate policy reset the writer"
        assert self.cli("send", "--text", "blocked", "--client", "second", ok=False)["code"] == "write_denied"
        self.cli("release", "--client", "first")
        self.cli("acquire", "--client", "second")
        assert self.status()["writer"] == "second"
        self.cli("client-close", "--client", "second")
        assert self.status()["writer"] == ""
        self.cli("policy", "shared")
        self.send_echo(client="third")

    def test_script_and_fail_fast(self):
        self.cli("disconnect")
        marker = "SCRIPT-" + uuid.uuid4().hex
        commands = f'''# quoted command file, with literal braces for paths
configure --host 127.0.0.1 --port {self.fixture.port} --user fixture --auth password --password-env SSHBROKER_TEST_PASSWORD --fingerprint {self.fixture.fingerprint} --telnet-port 0 --log-root {{{self.run_root / 'logs'}}}
connect
send --text "echo {marker}" --cr
wait-output --contains {marker} --after 0 --ms 5000
status
'''
        process = self.cli_process("--script", "-")
        out, err = process.communicate(commands.encode(), timeout=20)
        lines = [json.loads(line) for line in out.decode().splitlines()]
        assert process.returncode == 0, self.redact(err.decode())
        assert len(lines) == 5 and all(line["ok"] for line in lines)
        process = self.cli_process("--script", "-")
        out, _ = process.communicate(b"policy readonly\ninvalid-command\npolicy shared\n", timeout=10)
        assert process.returncode != 0
        assert len(out.decode().splitlines()) == 1
        assert self.status()["policy"] == "readonly"
        self.cli("policy", "shared")

    def test_key_authentication(self):
        self.connect(authentication="key", password="", privateKeyPath=str(self.fixture.key_path))
        self.send_echo()
        self.connect(authentication="key", password="", privateKeyPath=str(self.fixture.encrypted_key_path), keyPassphrase=self.fixture.passphrase)
        self.send_echo()

    def test_unexpected_disconnect_and_reconnect(self):
        snapshot = self.connect()
        marker = self.send_echo()
        generation = snapshot["generation"]
        folder = Path(self.status()["logDirectory"])
        waiting = self.cli_process("wait-output", "--contains", "never-" + uuid.uuid4().hex, "--after", self.status()["outputCursor"], "--ms", 12000)
        time.sleep(0.15)
        self.fixture.drop_all()
        fault = self.wait_status(lambda s: s["state"] == "faulted" and not s["telnetListening"] and not s["clients"], 7)
        out, _ = waiting.communicate(timeout=5)
        assert waiting.returncode != 0 and json.loads(out)["code"] == "connection_lost"
        assert self.cli("send", "--text", "after-failure", ok=False)["code"] == "not_connected"
        assert any(e["kind"] in ("transport_lost", "read_failed", "shell_closed", "shell_error") for e in map(json.loads, (folder / "events.jsonl").read_text().splitlines()))
        self.cli("connect")
        current = self.status()
        assert current["generation"] > generation
        new_tx = Path(current["logDirectory"]) / "tx.bin"
        assert new_tx.read_bytes() == b"", "Reconnect replayed a previous command"
        self.send_echo()

    def test_remote_shell_close(self):
        self.connect()
        self.cli("send", "--text", "__close_shell__", "--cr", ok=None)
        self.wait_status(lambda s: s["state"] == "faulted" and not s["telnetListening"], 6)
        self.cli("reconnect")
        self.send_echo()

    def test_network_blackhole(self):
        proxy = BlackholeProxy(self.fixture.port)
        try:
            self.connect(port=proxy.port)
            self.send_echo()
            start = time.monotonic()
            proxy.paused.set()
            fault = self.wait_status(lambda s: s["state"] == "faulted" and not s["telnetListening"], 8)
            assert fault["lastError"]["code"] in ("health_timeout", "transport_lost", "read_failed")
            assert time.monotonic() - start < 8
        finally:
            proxy.close()

    def test_refused_timeout_and_cancel(self):
        silent = SilentListener()
        try:
            self.configure(port=silent.port, connectTimeoutMs=650)
            start = time.monotonic()
            assert self.cli("connect", ok=False)["code"] == "connect_timeout"
            assert time.monotonic() - start < 4
            self.configure(port=silent.port, connectTimeoutMs=10000)
            pending = self.cli_process("connect")
            connecting = self.wait_status(lambda s: s["state"] == "connecting", 3)
            assert not connecting["telnetListening"] and connecting["localTelnetPort"] == 0, "Telnet must stay closed until SSH PTY is ready"
            self.cli("disconnect")
            pending.communicate(timeout=4)
            assert pending.returncode != 0 and self.status()["state"] == "disconnected"
        finally:
            silent.close()
        sock = socket.socket(); sock.bind(("127.0.0.1", 0)); port = sock.getsockname()[1]; sock.close()
        self.configure(port=port)
        # Windows filtering may turn an unused loopback port into a timeout instead of RST.
        assert self.cli("connect", ok=False)["code"] in ("connect_failed", "connect_timeout")
        reset = SilentListener(reset=True)
        try:
            self.configure(port=reset.port)
            assert self.cli("connect", ok=False)["code"] == "connect_failed"
        finally:
            reset.close()

    def test_logging_failure_and_redaction(self):
        self.connect(logLimitBytes=1024)
        for _ in range(4):
            self.send_echo()
        snapshot = self.status()
        assert not snapshot["logging"] and snapshot["logFailure"]
        assert snapshot["state"] == "connected"
        blocking_file = self.run_root / "not-a-directory"
        blocking_file.write_text("file")
        self.configure(logRoot=str(blocking_file))
        assert self.cli("connect", ok=False)["code"] == "connect_failed"
        for file in (self.run_root / "logs").rglob("events.jsonl"):
            data = file.read_text()
            assert self.fixture.password not in data and self.fixture.passphrase not in data

    def test_cli_watch_and_wait_timeout(self):
        self.connect()
        assert self.cli("wait-output", "--contains", "never-" + uuid.uuid4().hex, "--ms", 100, ok=False)["code"] == "wait_timeout"
        watch_path = self.run_root / "watch-rx.bin"
        with watch_path.open("wb") as output:
            watch = self.cli_process("watch", "--after", self.status()["outputCursor"], stdout=output)
            marker = self.send_echo()
            self.fixture.drop_all()
            watch.wait(timeout=7)
            assert watch.returncode == 4
        assert marker.encode() in watch_path.read_bytes()

    def test_queued_write_revocation(self):
        snapshot = self.connect(operationTimeoutMs=7000)
        self.cli("send", "--text", "__pause_reads__", "--cr")
        self.cli("wait-output", "--contains", "reads-paused", "--after", snapshot["outputCursor"], "--ms", 3000)
        writers = []
        try:
            for _ in range(16):
                writers.append(self.cli_process("send", "--text", "Q" * 4096))
            time.sleep(0.8)
            self.cli("policy", "readonly")
            self.fixture.pause_reads.clear()
            codes = []
            for writer in writers:
                out, _ = writer.communicate(timeout=10)
                codes.append(json.loads(out)["code"])
            assert "write_revoked" in codes, f"No queued writes were revoked: {codes}"
            assert self.status()["state"] == "connected"
            self.cli("policy", "shared")
            self.cli("send", "--hex", "03")
            self.send_echo()
        finally:
            self.fixture.pause_reads.clear()

    def test_slow_telnet_and_bounded_output(self):
        snapshot = self.connect()
        slow = socket.socket(); slow.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 1024)
        slow.connect(("127.0.0.1", snapshot["localTelnetPort"]))
        fast = socket.create_connection(("127.0.0.1", snapshot["localTelnetPort"]), timeout=2)
        fast.settimeout(0.2)
        stop = threading.Event(); count = [0]; tail = [b""]
        def drain():
            while not stop.is_set():
                try:
                    data = fast.recv(65536)
                    if not data:
                        return
                    count[0] += len(data); tail[0] = (tail[0] + data)[-65536:]
                except socket.timeout:
                    pass
                except OSError:
                    return
        reader = threading.Thread(target=drain, daemon=True); reader.start()
        try:
            before = self.wait_status(lambda s: len([c for c in s["clients"] if c["kind"] == "telnet"]) == 2)
            self.cli("send", "--text", "__flood__ 4194304", "--cr")
            deadline = time.monotonic() + 12
            while time.monotonic() < deadline and b"flood-done" not in tail[0]:
                time.sleep(0.1)
            assert count[0] >= 4194304 and b"flood-done" in tail[0], f"Healthy client did not drain flood: {count[0]}"
            self.wait_status(lambda s: len([c for c in s["clients"] if c["kind"] == "telnet"]) == 1, 7)
            assert self.status()["state"] == "connected"
            page = self.cli("output", "--after", "0")["result"]
            assert page["lostHistory"], "History did not remain bounded during flood"
            self.send_echo()
        finally:
            stop.set(); slow.close(); fast.close(); reader.join(timeout=2)

    def test_gui_real_bindings(self):
        self.configure(host="localhost")
        report = self.run_root / "gui-binding.json"
        process = subprocess.Popen([str(self.gui_exe), "--pipe", self.pipe, "--probe-output", str(report), "--probe-seconds", "25"],
                                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=CREATE_HIDDEN)
        self.processes.append(process)
        def wait_report(predicate, seconds=8):
            end = time.monotonic() + seconds; last = None
            while time.monotonic() < end:
                try:
                    last = json.loads(report.read_text())
                    if last.get("ok") and predicate(last):
                        return last
                except (OSError, json.JSONDecodeError):
                    pass
                if process.poll() is not None:
                    break
                time.sleep(0.1)
            raise AssertionError(f"WPF binding condition failed: {last}, process={process.poll()}")
        wait_report(lambda r: r["host"] == "localhost")
        configured = self.raw("configure", {"host": "127.0.0.1", "columns": 132, "rows": 36})["snapshot"]
        wait_report(lambda r: r["host"] == "127.0.0.1" and r["configRevision"] == configured["configRevision"])
        self.cli("connect")
        wait_report(lambda r: r["state"] == "connected")
        for policy in ("readonly", "single", "shared"):
            self.cli("policy", policy)
            wait_report(lambda r: r["policy"] == policy)
        self.send_echo()
        wait_report(lambda r: r["txBytes"] > 0 and r["rxBytes"] > 0)
        self.fixture.drop_all()
        wait_report(lambda r: r["state"] == "faulted")
        process.terminate(); process.wait(timeout=4)

    def test_client_limit_recovery(self):
        snapshot = self.connect()
        for i in range(64):
            self.cli("acquire", "--client", f"limit-{i}")
        assert self.cli("acquire", "--client", "overflow", ok=False)["code"] == "client_limit"
        rejected = socket.create_connection(("127.0.0.1", snapshot["localTelnetPort"]), timeout=2)
        rejected.settimeout(2)
        try:
            try:
                assert not rejected.recv(10)
            except ConnectionResetError:
                pass
        finally:
            rejected.close()
        assert self.status()["state"] == "connected"
        self.cli("client-close", "--client", "limit-0")
        accepted = socket.create_connection(("127.0.0.1", snapshot["localTelnetPort"]), timeout=2)
        accepted.settimeout(2)
        try:
            assert accepted.recv(32), "Telnet listener stopped after rejecting a full registry"
        finally:
            accepted.close()

    def test_gui_owns_service(self):
        pipe = "SshBroker.gui." + uuid.uuid4().hex
        report = self.run_root / "gui-owner.json"
        process = subprocess.Popen([str(self.gui_exe), "--pipe", pipe, "--probe-output", str(report), "--probe-own", "true", "--probe-seconds", "25"],
                                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=CREATE_HIDDEN)
        self.processes.append(process)
        def call(method, params=None):
            request = json.dumps({"id": uuid.uuid4().hex, "method": method, "params": params or {}}).encode()
            result = subprocess.run([str(self.cli_exe), "--pipe", pipe, "--rpc-timeout", "2000", "--json"], input=request,
                                    capture_output=True, timeout=5, creationflags=CREATE_HIDDEN)
            if result.returncode:
                raise AssertionError(self.redact(result.stderr.decode() + result.stdout.decode()))
            return json.loads(result.stdout)
        deadline = time.monotonic() + 8
        while time.monotonic() < deadline:
            try:
                call("status")
                break
            except AssertionError:
                time.sleep(0.1)
        else:
            raise AssertionError("GUI did not start its owned IPC service")
        call("configure", dict(host="127.0.0.1", port=self.fixture.port, username="fixture", password=self.fixture.password,
                               hostKeyFingerprint=self.fixture.fingerprint, telnetEnabled=False, logRoot=str(self.run_root / "gui-owned-logs")))
        assert call("connect")["snapshot"]["state"] == "connected"
        call("send", {"client": "agent", "text": "echo GUI_OWNED_SERVICE\r"})
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:
            try:
                displayed = json.loads(report.read_text())
                if displayed.get("ok") and displayed["state"] == "connected" and displayed["txBytes"] > 0:
                    break
            except (OSError, json.JSONDecodeError):
                pass
            time.sleep(0.1)
        else:
            raise AssertionError("GUI-owned service did not display CLI state changes")
        call("shutdown")
        process.wait(timeout=10)
        assert process.returncode == 0

    def test_duplicate_host_and_shutdown(self):
        duplicate = subprocess.run([str(self.host_exe), "--pipe", self.pipe], capture_output=True, timeout=5, creationflags=CREATE_HIDDEN)
        assert duplicate.returncode != 0 and b"service_exists" in duplicate.stderr
        self.connect()
        self.cli("shutdown")
        self.host.wait(timeout=10)
        assert self.host.returncode == 0

    def run(self, selected=None):
        names = (
            "defaults_and_contract", "config_atomicity", "host_key_and_auth", "password_cli_and_logs",
            "telnet_shared_pty", "policy_and_single_writer", "script_and_fail_fast", "key_authentication",
            "unexpected_disconnect_and_reconnect", "remote_shell_close", "network_blackhole",
            "refused_timeout_and_cancel", "logging_failure_and_redaction", "cli_watch_and_wait_timeout",
            "queued_write_revocation", "slow_telnet_and_bounded_output", "gui_real_bindings", "client_limit_recovery", "gui_owns_service", "duplicate_host_and_shutdown")
        for name in selected or names:
            self.case(name, getattr(self, "test_" + name))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--configuration", default="Release")
    parser.add_argument("--app-directory")
    parser.add_argument("--cases", nargs="+")
    args = parser.parse_args()
    suite = Suite(args.configuration, args.app_directory)
    try:
        suite.run(args.cases)
    finally:
        suite.close()
        result = dict(passed=sum(r["ok"] for r in suite.results), failed=sum(not r["ok"] for r in suite.results),
                      cases=suite.results, runtimeDirectory=str(suite.run_root), testedBinaryDirectory=suite.binary_directory,
                      testedAt=time.strftime("%Y-%m-%dT%H:%M:%S%z"))
        destination = ROOT / "artifacts" / "integration-results.json"
        destination.parent.mkdir(exist_ok=True)
        destination.write_text(json.dumps(result, indent=2, ensure_ascii=False), encoding="utf-8")
        print(json.dumps({"passed": result["passed"], "failed": result["failed"], "results": str(destination)}), flush=True)
    return int(any(not r["ok"] for r in suite.results))


if __name__ == "__main__":
    raise SystemExit(main())

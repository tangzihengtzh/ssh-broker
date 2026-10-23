"""Isolated loopback SSH fixture. No system SSH service or real device is used."""
from __future__ import annotations
import base64
import hashlib
import logging
import select
import socket
import struct
import threading
import time
import uuid
from pathlib import Path
import paramiko

logging.getLogger("paramiko").addHandler(logging.NullHandler())
logging.getLogger("paramiko").setLevel(logging.CRITICAL)


class SSHFixture:
    def __init__(self, root: Path):
        self.root = root
        root.mkdir(parents=True, exist_ok=True)
        self.key = paramiko.RSAKey.generate(2048)
        self.client_key = paramiko.RSAKey.generate(2048)
        self.password = "test-" + uuid.uuid4().hex
        self.passphrase = "key-" + uuid.uuid4().hex
        self.username = "fixture"
        self.key_path = root / "client_rsa.pem"
        self.encrypted_key_path = root / "client_rsa_encrypted.pem"
        self.client_key.write_private_key_file(str(self.key_path))
        self.client_key.write_private_key_file(str(self.encrypted_key_path), password=self.passphrase)
        self.fingerprint = "SHA256:" + base64.b64encode(hashlib.sha256(self.key.asbytes()).digest()).decode().rstrip("=")
        self.listener = socket.socket()
        self.listener.bind(("127.0.0.1", 0))
        self.listener.listen(16)
        self.listener.settimeout(0.2)
        self.port = self.listener.getsockname()[1]
        self.stop = threading.Event()
        self.health_silent = threading.Event()
        self.pause_reads = threading.Event()
        self.lock = threading.Lock()
        self.transports: list[paramiko.Transport] = []
        self.shells = 0
        self.connections = 0
        self.auth_attempts = 0
        self.received = bytearray()
        self.sizes: list[tuple[int, int]] = []
        self.thread = threading.Thread(target=self._accept, daemon=True)
        self.thread.start()

    def _accept(self):
        while not self.stop.is_set():
            try:
                connection, _ = self.listener.accept()
            except socket.timeout:
                continue
            except OSError:
                break
            threading.Thread(target=self._session, args=(connection,), daemon=True).start()

    def _session(self, connection):
        transport = paramiko.Transport(connection, default_window_size=32768)
        with self.lock:
            self.transports.append(transport)
            self.connections += 1
        try:
            transport.add_server_key(self.key)
            transport.start_server(server=Server(self, transport))
            while transport.is_active() and not self.stop.wait(0.05):
                # Paramiko owns accepted channels; callbacks launch shell/exec workers.
                channel = transport.accept(0.05)
                if channel is not None:
                    pass
        except (OSError, EOFError, paramiko.SSHException):
            pass
        finally:
            transport.close()

    def shell(self, channel, transport):
        with self.lock:
            self.shells += 1
        try:
            channel.sendall(b"fixture-ready> ")
            pending = bytearray()
            while not self.stop.is_set() and not channel.closed:
                while self.pause_reads.is_set() and not self.stop.wait(0.05):
                    pass
                data = channel.recv(4096)
                if not data:
                    break
                with self.lock:
                    self.received.extend(data)
                for value in data:
                    if value == 3:
                        pending.clear()
                        channel.sendall(b"^C\r\nfixture-ready> ")
                    elif value in (10, 13):
                        if not pending:
                            channel.sendall(b"\r\nfixture-ready> ")
                            continue
                        text = pending.decode("utf-8", "replace")
                        pending.clear()
                        if text == "__drop__":
                            transport.sock.shutdown(socket.SHUT_RDWR)
                            transport.sock.close()
                            return
                        if text == "__close_shell__":
                            channel.sendall(b"shell-bye\r\n")
                            channel.close()
                            return
                        if text == "__pause_reads__":
                            self.pause_reads.set()
                            channel.sendall(b"reads-paused\r\n")
                            continue
                        if text.startswith("__flood__ "):
                            size = min(int(text.split()[1]), 16 * 1024 * 1024)
                            block = b"F" * 4096
                            while size:
                                chunk = block[:min(size, len(block))]
                                channel.sendall(chunk)
                                size -= len(chunk)
                            channel.sendall(b"\r\nflood-done\r\nfixture-ready> ")
                        elif text.startswith("echo "):
                            channel.sendall(text[5:].encode() + b"\r\nfixture-ready> ")
                        else:
                            channel.sendall(("fixture: " + text + "\r\nfixture-ready> ").encode())
                    else:
                        pending.append(value)
                        if len(pending) > 65536:
                            channel.close()
        except (OSError, EOFError, paramiko.SSHException):
            pass

    def drop_all(self):
        with self.lock:
            active = list(self.transports)
        for transport in active:
            try:
                transport.sock.shutdown(socket.SHUT_RDWR)
                transport.sock.close()
            except OSError:
                pass
            transport.close()

    def close(self):
        self.stop.set()
        self.listener.close()
        self.drop_all()
        self.thread.join(timeout=2)


class Server(paramiko.ServerInterface):
    def __init__(self, owner, transport):
        self.owner = owner
        self.transport = transport

    def get_allowed_auths(self, username):
        return "password,publickey"

    def check_auth_password(self, username, password):
        with self.owner.lock:
            self.owner.auth_attempts += 1
        return paramiko.AUTH_SUCCESSFUL if username == self.owner.username and password == self.owner.password else paramiko.AUTH_FAILED

    def check_auth_publickey(self, username, key):
        return paramiko.AUTH_SUCCESSFUL if username == self.owner.username and key == self.owner.client_key else paramiko.AUTH_FAILED

    def check_channel_request(self, kind, chanid):
        return paramiko.OPEN_SUCCEEDED if kind == "session" else paramiko.OPEN_FAILED_ADMINISTRATIVELY_PROHIBITED

    def check_channel_pty_request(self, channel, term, width, height, pixelwidth, pixelheight, modes):
        with self.owner.lock:
            self.owner.sizes.append((width, height))
        return True

    def check_channel_window_change_request(self, channel, width, height, pixelwidth, pixelheight):
        with self.owner.lock:
            self.owner.sizes.append((width, height))
        return True

    def check_channel_shell_request(self, channel):
        threading.Thread(target=self.owner.shell, args=(channel, self.transport), daemon=True).start()
        return True

    def check_channel_exec_request(self, channel, command):
        def execute():
            if self.owner.health_silent.is_set():
                while not self.owner.stop.wait(0.1) and not channel.closed:
                    pass
                return
            try:
                channel.send_exit_status(0 if command == b"true" else 127)
                channel.close()
            except (OSError, EOFError):
                pass
        threading.Thread(target=execute, daemon=True).start()
        return True


class BlackholeProxy:
    def __init__(self, upstream_port: int):
        self.listener = socket.socket()
        self.listener.bind(("127.0.0.1", 0))
        self.listener.listen()
        self.listener.settimeout(0.2)
        self.port = self.listener.getsockname()[1]
        self.upstream_port = upstream_port
        self.paused = threading.Event()
        self.stop = threading.Event()
        self.sockets = []
        self.thread = threading.Thread(target=self._run, daemon=True)
        self.thread.start()

    def _run(self):
        try:
            while not self.stop.is_set():
                try:
                    client, _ = self.listener.accept()
                    break
                except socket.timeout:
                    continue
            else:
                return
            upstream = socket.create_connection(("127.0.0.1", self.upstream_port), timeout=2)
            self.sockets = [client, upstream]
            while not self.stop.is_set():
                ready, _, _ = select.select(self.sockets, [], [], 0.1)
                for source in ready:
                    data = source.recv(65536)
                    if not data:
                        return
                    if not self.paused.is_set():
                        (upstream if source is client else client).sendall(data)
        except OSError:
            pass
        finally:
            for item in self.sockets:
                item.close()

    def close(self):
        self.stop.set()
        self.listener.close()
        for item in self.sockets:
            item.close()
        self.thread.join(timeout=2)


class SilentListener:
    def __init__(self, reset=False):
        self.reset = reset
        self.listener = socket.socket()
        self.listener.bind(("127.0.0.1", 0))
        self.listener.listen()
        self.listener.settimeout(0.1)
        self.port = self.listener.getsockname()[1]
        self.stop = threading.Event()
        self.connections = []
        self.thread = threading.Thread(target=self._run, daemon=True)
        self.thread.start()

    def _run(self):
        while not self.stop.is_set():
            try:
                connection, _ = self.listener.accept()
                if self.reset:
                    connection.setsockopt(socket.SOL_SOCKET, socket.SO_LINGER, struct.pack('HH', 1, 0))
                    connection.close()
                else:
                    self.connections.append(connection)
            except socket.timeout:
                continue
            except OSError:
                break

    def close(self):
        self.stop.set()
        self.listener.close()
        for connection in self.connections:
            connection.close()
        self.thread.join(timeout=2)

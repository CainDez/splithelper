"""Check the bundled binary on random loopback ports, without touching the live helper."""
import hashlib
import http.server
import json
import pathlib
import socket
import struct
import subprocess
import tempfile
import threading
import time


def receive(sock, count):
    data = b""
    while len(data) < count:
        part = sock.recv(count - len(data))
        if not part:
            raise RuntimeError("Unexpected SOCKS EOF")
        data += part
    return data


def main():
    repo = pathlib.Path(__file__).resolve().parents[2]
    component = repo / "desktop/SplitHelper/components/direct-helper"
    executable = component / "mullvad-direct-helper.exe"
    manifest = json.loads((component / "manifest.json").read_text(encoding="utf-8-sig"))
    assert hashlib.sha256(executable.read_bytes()).hexdigest().lower() == manifest["sha256"].lower()
    assert hashlib.sha256((repo / "direct.yaml").read_bytes()).hexdigest().lower() == manifest["configSha256"].lower()
    output = (repo / "out").resolve()
    output.mkdir(exist_ok=True)
    temporary = tempfile.TemporaryDirectory(prefix="helper-smoke-", dir=output)
    directory = pathlib.Path(temporary.name).resolve()
    assert directory.is_relative_to(output)
    child = None
    server = None
    try:
        checked = subprocess.run([str(executable), "-t", "-d", str(directory), "-f", str(repo / "direct.yaml")], capture_output=True, timeout=20)
        assert checked.returncode == 0, "Bundled direct.yaml validation failed"
        with socket.socket() as reservation:
            reservation.bind(("127.0.0.1", 0))
            port = reservation.getsockname()[1]
        assert port != 18997
        config = directory / "smoke.yaml"
        config.write_text(f"socks-port: {port}\nallow-lan: false\nbind-address: 127.0.0.1\nipv6: false\nmode: rule\nlog-level: silent\ndns:\n  enable: false\nrules:\n  - MATCH,DIRECT\n", encoding="utf-8")

        class Handler(http.server.BaseHTTPRequestHandler):
            def do_GET(self):
                self.send_response(200)
                self.end_headers()
                self.wfile.write(b"splithelper-loopback-ok")

            def log_message(self, *_):
                pass

        server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        child = subprocess.Popen([str(executable), "-d", str(directory), "-f", str(config)], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        deadline = time.monotonic() + 10
        while True:
            if child.poll() is not None:
                raise RuntimeError("Isolated helper exited before listening")
            try:
                client = socket.create_connection(("127.0.0.1", port), timeout=2)
                break
            except OSError:
                if time.monotonic() > deadline:
                    raise
                time.sleep(0.1)
        with client:
            client.sendall(b"\x05\x01\x00")
            assert receive(client, 2) == b"\x05\x00"
            client.sendall(b"\x05\x01\x00\x01\x7f\x00\x00\x01" + struct.pack("!H", server.server_port))
            header = receive(client, 4)
            assert header[:2] == b"\x05\x00"
            length = {1: 4, 4: 16}.get(header[3])
            if header[3] == 3:
                length = receive(client, 1)[0]
            assert length is not None
            receive(client, length + 2)
            client.sendall(b"GET /health HTTP/1.0\r\nHost: localhost\r\n\r\n")
            response = b""
            while part := client.recv(4096):
                response += part
            assert b"200 OK" in response and b"splithelper-loopback-ok" in response
        print(json.dumps({"version": manifest["version"], "hashes": "passed", "configuration": "passed", "socks_and_http_loopback": "passed", "live_port_18997": "untouched"}))
    finally:
        if child is not None and child.poll() is None:
            child.terminate()
            child.wait(timeout=10)
        if server is not None:
            server.shutdown()
            server.server_close()
        temporary.cleanup()


if __name__ == "__main__":
    main()

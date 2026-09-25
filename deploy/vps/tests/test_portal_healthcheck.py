"""Run the real Compose-rendered Portal probe against controlled HTTP responses.

Uses Docker Compose for configuration rendering only; no Docker daemon or
application containers are used. All fixtures contain dummy configuration.
"""
import json
import os
from pathlib import Path
import shutil
import socket
import socketserver
import subprocess
import tempfile
import threading
import unittest


class PortalHealthcheckTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.fixture = tempfile.TemporaryDirectory(prefix="portal-healthcheck-")
        root = Path(cls.fixture.name)
        cls.addClassCleanup(cls.fixture.cleanup)
        overlay = Path(__file__).resolve().parents[1] / "compose.images.yml"
        text = overlay.read_text(encoding="utf-8")
        text = text.replace("__API_IMAGE__", "example/api@sha256:" + "a" * 64)
        text = text.replace("__PORTAL_IMAGE__", "example/portal@sha256:" + "b" * 64)
        (root / "overlay.yml").write_text(text, encoding="utf-8")
        (root / "base.yml").write_text(
            "services:\n  api:\n    image: example/api\n"
            "  portal:\n    image: example/portal\n", encoding="utf-8"
        )
        (root / "test.env").write_text(
            "INFOBIP_API_KEY=test-only\nINFOBIP_WHATSAPP_SENDER=test-only\n"
            "INFOBIP_WEBHOOK_SECRET=test-only\n", encoding="utf-8"
        )
        env = dict(os.environ, DOCKER_CONFIG=str(root))
        resolved = subprocess.run(
            ["docker", "compose", "-p", "portal-healthcheck-test", "--env-file",
             str(root / "test.env"), "-f", str(root / "base.yml"), "-f",
             str(root / "overlay.yml"), "config", "--format", "json"],
            env=env, capture_output=True, text=True, check=True, timeout=30,
        )
        probe = json.loads(resolved.stdout)["services"]["portal"]["healthcheck"]["test"]
        if probe[:3] != ["CMD", "bash", "-ec"] or len(probe) != 4:
            raise AssertionError("Expected an exec-form Bash probe")
        cls.script = probe[3]
        cls.bash = os.environ.get("HEALTHCHECK_TEST_BASH") or shutil.which("bash")
        if not cls.bash:
            raise RuntimeError("Bash is required to execute the production probe")

    def run_probe(self, port):
        # Only the fixture port changes; execute the actual rendered shell code.
        script = self.script.replace("/127.0.0.1/8080", f"/127.0.0.1/{port}")
        if os.name == "nt":
            script = "export PATH=/usr/bin:/bin:$PATH; " + script
        return subprocess.run(
            [self.bash, "-ec", script], capture_output=True, text=True, timeout=5,
        )

    def test_shell_syntax(self):
        result = subprocess.run(
            [self.bash, "-n", "-c", self.script], capture_output=True, text=True,
            timeout=5,
        )
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_http_responses(self):
        for status, accepted in [(200, True), (301, True), (302, True), (303, True),
                                 (307, True), (308, True), (401, False),
                                 (404, False), (500, False)]:
            with self.subTest(status=status):
                response = (f"HTTP/1.1 {status} Test\r\nContent-Length: 0\r\n"
                            "Connection: close\r\n\r\n").encode("ascii")

                class Handler(socketserver.BaseRequestHandler):
                    def handle(self):
                        self.request.settimeout(3)
                        request = b""
                        while b"\r\n\r\n" not in request:
                            chunk = self.request.recv(4096)
                            if not chunk:
                                return
                            request += chunk
                        self.request.sendall(response)

                with socketserver.TCPServer(("127.0.0.1", 0), Handler) as server:
                    server.timeout = 4
                    thread = threading.Thread(target=server.handle_request, daemon=True)
                    thread.start()
                    result = self.run_probe(server.server_address[1])
                    thread.join(timeout=5)
                self.assertEqual(result.returncode == 0, accepted, result.stderr)

    def test_closed_port_fails(self):
        with socket.socket() as listener:
            listener.bind(("127.0.0.1", 0))
            port = listener.getsockname()[1]
        self.assertNotEqual(self.run_probe(port).returncode, 0)


if __name__ == "__main__":
    unittest.main(verbosity=2)

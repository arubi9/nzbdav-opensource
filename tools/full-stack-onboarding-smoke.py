#!/usr/bin/env python3
"""Browser-contract fresh-install onboarding smoke for the five-service stack.

The harness never reads product-generated credentials or calls private setup
APIs. All NZBDAV setup mutations are URL-encoded React Router actions with the
browser session and rotating CSRF cookie/token. Protocol fakes run in an
isolated Docker container: it is not a Compose service and is reached over
Docker networking, including native Linux engines.
"""
from __future__ import annotations

import argparse
import errno
import hashlib
import ipaddress
import html
import json
import os
import re
import secrets
import signal
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
from http.cookiejar import CookieJar
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parent.parent
COMPOSE_FILE = ROOT / "docker-compose.full-stack.yml"
SERVICES = ("nzbdav", "jellyfin", "sonarr", "radarr", "prowlarr")
SERVICE_CONTAINER_PORTS = {"nzbdav": 3000, "jellyfin": 8096, "sonarr": 8989, "radarr": 7878, "prowlarr": 9696}
CANONICAL_VOLUMES = frozenset(("nzbdav_config", "nzbdav_media", "completed_downloads", "jellyfin_config", "jellyfin_cache", "sonarr_config", "radarr_config", "prowlarr_config"))
REQUIRED_VOLUME_MOUNTS = frozenset((
    ("nzbdav", "nzbdav_config", "/config", False), ("nzbdav", "nzbdav_media", "/media/nzbdav", False),
    ("nzbdav", "completed_downloads", "/data/completed-downloads", False), ("nzbdav", "sonarr_config", "/bootstrap/sonarr", True),
    ("nzbdav", "radarr_config", "/bootstrap/radarr", True), ("nzbdav", "prowlarr_config", "/bootstrap/prowlarr", True),
    ("jellyfin", "jellyfin_config", "/config", False), ("jellyfin", "jellyfin_cache", "/cache", False),
    ("jellyfin", "nzbdav_media", "/media/nzbdav", False), ("sonarr", "sonarr_config", "/config", False),
    ("sonarr", "completed_downloads", "/data/completed-downloads", False), ("radarr", "radarr_config", "/config", False),
    ("radarr", "completed_downloads", "/data/completed-downloads", False), ("prowlarr", "prowlarr_config", "/config", False),
))
ADMIN_USER = "nzbdav-e2e-admin"


class Failure(RuntimeError):
    pass


def assert_normalized_compose_topology(value: Any) -> dict[str, Any]:
    """Validate only service/volume mount metadata from normalized Compose."""
    if not isinstance(value, dict) or set(value.get("services", {})) != set(SERVICES):
        raise Failure("Compose service set is not exactly five")
    volumes = value.get("volumes")
    if not isinstance(volumes, dict) or set(volumes) != CANONICAL_VOLUMES:
        raise Failure("Compose named-volume set is not exactly the canonical eight")
    actual: set[tuple[str, str, str, bool]] = set()
    services = value["services"]
    if not isinstance(services, dict):
        raise Failure("normalized Compose services were not an object")
    for service, config in services.items():
        if not isinstance(config, dict) or not isinstance(config.get("volumes", []), list):
            raise Failure("normalized Compose service mounts were not a list")
        for mount in config["volumes"]:
            if not isinstance(mount, dict):
                raise Failure("normalized Compose mount was not an object")
            # Compose --format json represents named mounts using these fields.
            if set(mount) - {"type", "source", "target", "read_only", "bind", "volume", "tmpfs", "consistency"}:
                raise Failure("normalized Compose mount had an unexpected field")
            source, target = mount.get("source"), mount.get("target")
            if mount.get("type") == "volume" and source in CANONICAL_VOLUMES:
                if not isinstance(target, str) or not isinstance(mount.get("read_only", False), bool):
                    raise Failure("normalized Compose named mount had an invalid type")
                actual.add((service, source, target, mount.get("read_only", False)))
    if actual != REQUIRED_VOLUME_MOUNTS:
        raise Failure("Compose named-volume mount relationships did not match the canonical topology")
    return {"service_names": sorted(services), "named_volume_names": sorted(volumes), "named_volume_count": len(volumes), "required_named_mount_count": len(actual)}


def choose_port() -> int:
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return int(sock.getsockname()[1])


FAKE_PROGRAM = r'''
import os, socket, threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlsplit
USER = os.environ["E2E_NNTP_USER"]
PASSWORD = os.environ["E2E_NNTP_PASSWORD"]
APIKEY = os.environ["E2E_INDEXER_KEY"]
# These records deliberately contain only operation/outcome metadata.
audit = []
lock = threading.Lock()
def note(event, ok):
    with lock:
        if len(audit) < 128: audit.append({"event": event, "ok": bool(ok)})
def nntp_client(client):
    with client:
        client.settimeout(10); client.sendall(b"200 NZBDAV E2E ready\r\n")
        state = "new"; buffered = b""
        while True:
            try: part = client.recv(4096)
            except OSError: return
            if not part: return
            buffered += part
            while b"\n" in buffered:
                line, buffered = buffered.split(b"\n", 1)
                fields = line.strip().decode("utf-8", "replace").split(" ", 2)
                command = fields[0].upper() if fields else ""
                if command == "AUTHINFO" and len(fields) >= 3 and fields[1].upper() == "USER":
                    ok = state == "new" and fields[2] == USER; note("nntp-user", ok)
                    state = "user" if ok else "failed"
                    client.sendall(b"381 password required\r\n" if ok else b"481 authentication rejected\r\n")
                elif command == "AUTHINFO" and len(fields) >= 3 and fields[1].upper() == "PASS":
                    ok = state == "user" and fields[2] == PASSWORD; note("nntp-pass", ok)
                    state = "authenticated" if ok else "failed"
                    client.sendall(b"281 authentication accepted\r\n" if ok else b"481 authentication rejected\r\n")
                elif command == "QUIT":
                    client.sendall(b"205 closing\r\n"); return
                elif state == "authenticated": client.sendall(b"200 command accepted\r\n")
                else: client.sendall(b"480 authentication required\r\n")
def nntp():
    with socket.socket() as server:
        server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        server.bind(("0.0.0.0", int(os.environ.get("E2E_NNTP_PORT", "119")))); server.listen(16)
        while True:
            client, _ = server.accept()
            threading.Thread(target=nntp_client, args=(client,), daemon=True).start()
class Web(BaseHTTPRequestHandler):
    def do_GET(self):
        parsed = urlsplit(self.path); q = parse_qs(parsed.query, keep_blank_values=True)
        op = q.get("t", [""])[0].lower(); ok = q.get("apikey", [None])[0] == APIKEY
        if parsed.path == "/audit":
            with lock:
                counts = {}
                for event in audit:
                    key = event["event"] + ("-ok" if event["ok"] else "-rejected")
                    counts[key] = counts.get(key, 0) + 1
            body = __import__("json").dumps(counts, sort_keys=True).encode()
            self.send_response(200); self.send_header("Content-Type", "application/json"); self.send_header("Content-Length", str(len(body))); self.end_headers(); self.wfile.write(body); return
        if op not in ("caps", "search", "tvsearch", "movie"):
            note("newznab-other", False); self.send_error(400); return
        # Newznab capability discovery is deliberately public. This mirrors
        # NewznabCapabilityClient.BuildCapabilityUri(), which appends only
        # t=caps. Search endpoints, by contrast, must carry the exact key.
        if op == "caps":
            note("newznab-caps-unauthenticated", q.get("apikey", [None])[0] is None)
            body = b'<?xml version="1.0"?><caps><server title="NZBDAV E2E"/><limits max="100" default="50"/><search available="yes" supportedParams="q"/><tv-search available="yes" supportedParams="q,season,ep"/><movie-search available="yes" supportedParams="q,imdbid"/><categories><category id="2000" name="Movies"><subcat id="2040" name="HD"/></category><category id="5000" name="TV"><subcat id="5040" name="HD"/></category></categories></caps>'
        else:
            note("newznab-" + op, ok)
            if not ok: self.send_error(403); return
            # Prowlarr validates that its create-time Newznab probe returns a
            # release, not merely a syntactically valid empty RSS feed.
            body = b'<?xml version="1.0"?><rss version="2.0"><channel><title>NZBDAV E2E</title><item><title>NZBDAV E2E probe</title><guid isPermaLink="false">e2e-probe</guid><link>http://e2e.invalid/nzb</link><pubDate>Wed, 01 Jan 2025 00:00:00 GMT</pubDate><enclosure url="http://e2e.invalid/nzb" length="1" type="application/x-nzb"/><category>5000</category><newznab:attr xmlns:newznab="http://www.newznab.com/DTD/2010/feeds/attributes/" name="category" value="5000"/><newznab:attr xmlns:newznab="http://www.newznab.com/DTD/2010/feeds/attributes/" name="category" value="2000"/><newznab:attr xmlns:newznab="http://www.newznab.com/DTD/2010/feeds/attributes/" name="size" value="1"/></item></channel></rss>'
        self.send_response(200); self.send_header("Content-Type", "application/xml"); self.send_header("Content-Length", str(len(body))); self.end_headers(); self.wfile.write(body)
    def log_message(self, *args): pass
threading.Thread(target=nntp, daemon=True).start()
ThreadingHTTPServer(("0.0.0.0", int(os.environ.get("E2E_NEWZNAB_PORT", "8080"))), Web).serve_forever()
'''


class Redactor:
    """In-memory secret registry; every retained value passes through this."""
    def __init__(self, values: list[str]) -> None:
        self.values = values

    def add(self, value: Any) -> None:
        if isinstance(value, str) and value and value not in self.values:
            self.values.append(value)

    def redact(self, value: Any) -> Any:
        if isinstance(value, dict):
            return {str(k): "[REDACTED]" if any(x in str(k).lower() for x in ("pass", "token", "key", "grant", "secret", "authorization", "cookie")) else self.redact(v) for k, v in value.items()}
        if isinstance(value, list): return [self.redact(v) for v in value]
        if isinstance(value, str):
            for secret in self.values:
                value = value.replace(secret, "[REDACTED]")
            return value
        return value


class Harness:
    def __init__(self, keep: bool) -> None:
        self.project = "nzbdav-e2e-" + secrets.token_hex(6)
        self.owner_label = "nzbdav.e2e.owner=" + self.project
        self.fake_name = self.project + "-fakes"
        self.fake_network = self.project + "-fakes-net"
        self.owned_ids: dict[str, set[str]] = {"container": set(), "volume": set(), "network": set()}
        # Ask Docker to allocate every published host port atomically. A
        # bind-probe followed by Compose up has an unavoidable TOCTOU race.
        self.ports = {name: 0 for name in SERVICES}
        # Fakes have no host publication and live only on their disposable
        # network, so fixed internal ports cannot collide with the host.
        self.fake_nntp_port = 4119
        self.fake_newznab_port = 8080
        self.fake_stack_ip = ""
        self.admin_password = "e2e-" + secrets.token_urlsafe(18)
        self.nntp_user = "e2e-nntp-user"
        self.nntp_password = "e2e-" + secrets.token_urlsafe(18)
        self.indexer_key = "e2e-" + secrets.token_urlsafe(18)
        self.redactor = Redactor([self.admin_password, self.nntp_user, self.nntp_password, self.indexer_key])
        self.keep = keep
        self.keep_project = False
        self.phase = "preflight"
        self.started = time.monotonic()
        self.artifacts = Path(tempfile.mkdtemp(prefix="nzbdav-onboarding-e2e-"))
        self.evidence: dict[str, Any] = {"project": self.project, "ports": self.ports, "phases": []}
        self.compose = ["docker", "compose", "--project-name", self.project, "--file", str(COMPOSE_FILE)]
        self.jar = CookieJar()
        self.browser = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.jar))
        self.jellyfin_token = ""
        self.cleanup_errors: list[str] = []

    def record(self, phase: str, **data: Any) -> None:
        self.phase = phase
        self.evidence["phases"].append({"phase": phase, "seconds": round(time.monotonic() - self.started, 1), **self.redactor.redact(data)})
        self._write_evidence()

    def _write_evidence(self) -> None:
        output = json.dumps(self.redactor.redact(self.evidence), indent=2, sort_keys=True)
        if any(value in output for value in self.redactor.values): raise Failure("secret reached retained evidence")
        (self.artifacts / "evidence.json").write_text(output, encoding="utf-8")

    def run(self, args: list[str], timeout: int = 120, check: bool = True, env: dict[str, str] | None = None) -> subprocess.CompletedProcess[str]:
        # Docker and child diagnostics are not guaranteed to use the Windows
        # console code page. Decode a bounded UTF-8 replacement stream so a
        # malformed byte cannot mask the actual setup failure or bypass the
        # existing redaction path.
        result = subprocess.run(args, cwd=ROOT, env=env, text=True, encoding="utf-8", errors="replace", stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=timeout)
        if result.returncode and check:
            raise Failure(f"command failed ({result.returncode}): {' '.join(args)}\n{self.redactor.redact(result.stdout)[-30000:]}")
        return result

    def compose_run(self, args: list[str], timeout: int = 120, check: bool = True) -> subprocess.CompletedProcess[str]:
        return self.run(self.compose + args, timeout, check)

    def http_raw(self, url: str, method: str = "GET", form: dict[str, str] | None = None, headers: dict[str, str] | None = None, timeout: int = 90) -> tuple[int, str, Any]:
        data = urllib.parse.urlencode(form).encode() if form is not None else None
        request_headers = {"Accept": "application/json", **(headers or {})}
        if form is not None: request_headers["Content-Type"] = "application/x-www-form-urlencoded"
        request = urllib.request.Request(url, data=data, headers=request_headers, method=method)
        try:
            with self.browser.open(request, timeout=timeout) as response:
                return response.status, response.read(8 * 1024 * 1024).decode("utf-8", "replace"), response.headers
        except urllib.error.HTTPError as error:
            return error.code, error.read(1024 * 1024).decode("utf-8", "replace"), error.headers

    def browser_json(self, path: str, method: str = "GET", form: dict[str, str] | None = None, timeout: int = 90) -> Any:
        base = f"http://127.0.0.1:{self.ports['nzbdav']}"
        headers = {"Origin": base} if method == "POST" else {}
        status, raw, _ = self.http_raw(base + path, method, form, headers, timeout)
        if status != 200:
            raise Failure(f"browser request {path} expected 200, got {status}: {self.redactor.redact(raw[:1000])}")
        try: return json.loads(raw)
        except json.JSONDecodeError: raise Failure(f"browser request {path} did not return JSON")

    def browser_action(self, fields: dict[str, str], timeout: int) -> Any:
        """Submit the exact HTML form contract and follow its normal redirect."""
        base = f"http://127.0.0.1:{self.ports['nzbdav']}"
        status, raw, _ = self.http_raw(base + "/onboarding", "POST", fields, {"Origin": base}, timeout)
        if status != 200:
            raise Failure(f"onboarding form expected final 200, got {status}: {self.redactor.redact(raw[:1000])}")
        try: return json.loads(raw)
        except json.JSONDecodeError: return {"redirected": True}

    def initial_csrf(self, timeout: int = 90) -> str:
        base = f"http://127.0.0.1:{self.ports['nzbdav']}"
        status, page, headers = self.http_raw(base + "/onboarding", headers={"Accept": "text/html"}, timeout=timeout)
        if status != 200: raise Failure(f"onboarding page unavailable: {status}")
        # React's server renderer is free to order hidden-input attributes;
        # identify the csrf input, then its value instead of assuming order.
        unescaped = html.unescape(page)
        match = (re.search(r'<input\b(?=[^>]*\bname=["\']csrfToken["\'])[^>]*\bvalue=["\']([A-Za-z0-9_-]+)["\']', unescaped)
                 or re.search(r'<meta\b(?=[^>]*\bname=["\']csrf-token["\'])[^>]*\bcontent=["\']([A-Za-z0-9_-]+)["\']', unescaped)
                 # React Router may serialize loader data into its hydration
                 # script before the form is server-rendered.
                 or re.search(r'csrfToken(?:"|&quot;)\s*:\s*(?:"|&quot;)([A-Za-z0-9_-]+)', unescaped))
        header_token = headers.get("X-CSRF-Token")
        if match: token = match.group(1)
        elif header_token and re.fullmatch(r"[A-Za-z0-9_-]+", header_token): token = header_token
        else: raise Failure("onboarding page omitted CSRF form token")
        self.redactor.add(token)
        return token

    def csrf(self) -> str:
        base = f"http://127.0.0.1:{self.ports['nzbdav']}"
        status, raw, headers = self.http_raw(base + "/api/csrf-token")
        token = headers.get("X-CSRF-Token")
        if status != 200 or not token or not re.fullmatch(r"[A-Za-z0-9_-]+", token):
            raise Failure(f"browser CSRF refresh failed ({status}): {self.redactor.redact(raw[:500])}")
        self.redactor.add(token)
        return token

    def action(self, fields: dict[str, str], token: str | None = None, timeout: int = 600) -> Any:
        fields = {**fields, "csrfToken": token or self.csrf()}
        # This is exactly the React Router <Form method="POST"> encoding.
        return self.browser_action(fields, timeout)

    def wait_services(self, services: tuple[str, ...], timeout: int = 420) -> dict[str, str]:
        end = time.monotonic() + timeout; latest: dict[str, str] = {}
        while time.monotonic() < end:
            all_healthy = True
            for service in services:
                ident = self.compose_run(["ps", "-q", service], check=False).stdout.strip()
                state = self.run(["docker", "inspect", "--format", "{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}", ident], check=False).stdout.strip() if ident else "missing"
                latest[service] = state; all_healthy &= state == "healthy"
            if all_healthy:
                return latest
            time.sleep(3)
        raise Failure("timeout waiting for healthy services: " + repr(latest))

    def wait_healthy(self, timeout: int = 420) -> None:
        self.record("healthy", services=self.wait_services(SERVICES, timeout))

    @staticmethod
    def _is_connection_refused(error: urllib.error.URLError) -> bool:
        # Docker Desktop may briefly report container health before its host
        # port forwarder accepts connections. Retry that specific, transient
        # transport condition only; TLS/DNS/timeouts are distinct failures.
        reason = error.reason
        return (isinstance(reason, ConnectionRefusedError)
                or getattr(reason, "errno", None) == errno.ECONNREFUSED
                or getattr(reason, "winerror", None) == 10061)

    def wait_for_public_frontend(self, timeout: float = 60) -> None:
        """Boundedly prove the restarted published frontend serves a browser session."""
        end = time.monotonic() + timeout
        refused = 0
        while True:
            remaining = end - time.monotonic()
            if remaining <= 0:
                raise Failure(f"public frontend refused connections for {timeout}s after restart ({refused} attempts)")
            try:
                # Reuse the authenticated browser session. A GET of the
                # onboarding page can intentionally issue a fresh anonymous
                # session after setup, so it is not a restart-session probe.
                # The public status route proves both host publication and
                # the session needed by the final Ready assertion.
                self.browser_json("/api/setup/status", timeout=min(5, remaining))
                self.record("restart-public-frontend-ready", ports=self.ports, connection_refused_retries=refused)
                return
            except urllib.error.URLError as error:
                if not self._is_connection_refused(error):
                    raise Failure("public frontend readiness transport failure: " + type(error.reason).__name__) from error
                refused += 1
                time.sleep(min(1, max(0, end - time.monotonic())))

    def restart_and_wait_for_public_frontend(self) -> None:
        self.compose_run(["restart"], timeout=180)
        # Compose/Docker Desktop can replace host forwarding during restart;
        # never assume the pre-restart ephemeral publication remains current.
        self.discover_published_ports()
        self.record("restart-ports-resolved", ports=self.ports)
        self.wait_healthy(300)
        self.wait_for_public_frontend()

    def start_fakes(self) -> None:
        network = self.run(["docker", "network", "create", "--label", self.owner_label,
            "--label", f"com.docker.compose.project={self.project}", self.fake_network], timeout=30).stdout.strip()
        if network: self.owned_ids["network"].add(network)
        env = ["-e", f"E2E_NNTP_USER={self.nntp_user}", "-e", f"E2E_NNTP_PASSWORD={self.nntp_password}", "-e", f"E2E_INDEXER_KEY={self.indexer_key}", "-e", f"E2E_NNTP_PORT={self.fake_nntp_port}", "-e", f"E2E_NEWZNAB_PORT={self.fake_newznab_port}"]
        fake = self.run(["docker", "run", "--detach", "--name", self.fake_name, "--network", self.fake_network,
            "--label", self.owner_label, "--label", f"com.docker.compose.project={self.project}", *env,
            "python:3.12-alpine", "python", "-u", "-c", FAKE_PROGRAM], timeout=180).stdout.strip()
        if fake: self.owned_ids["container"].add(fake)
        self.record("fakes-started", topology="separate-container-and-network", owner_label=True)

    def fake_audit(self) -> list[dict[str, Any]]:
        # No fake logs or environment are retained. /audit supplies only a
        # bounded count summary with no credential values.
        return []

    def bootstrap_jellyfin(self) -> None:
        base = f"http://127.0.0.1:{self.ports['jellyfin']}"
        def call(path: str, body: Any | None = None, headers: dict[str, str] | None = None, expected: int = 204) -> Any:
            data = None if body is None else json.dumps(body).encode()
            request = urllib.request.Request(base + path, data=data, headers={"Content-Type": "application/json", **(headers or {})}, method="POST")
            try:
                with urllib.request.urlopen(request, timeout=60) as response:
                    raw = response.read().decode("utf-8", "replace")
                    if response.status != expected: raise Failure(f"Jellyfin {path}: {response.status}")
                    return json.loads(raw) if raw else None
            except urllib.error.HTTPError as error: raise Failure(f"Jellyfin {path}: {error.code}") from error
        end = time.monotonic() + 90
        while True:
            try:
                with urllib.request.urlopen(base + "/health", timeout=5) as response:
                    if response.status in (200, 204): break
            except urllib.error.URLError: pass
            if time.monotonic() >= end: raise Failure("Jellyfin health did not become ready")
            time.sleep(1)
        call("/Startup/Configuration", {"ServerName": "NZBDAV E2E", "UICulture": "en-US", "MetadataCountryCode": "US", "PreferredMetadataLanguage": "en"})
        end = time.monotonic() + 90
        while True:
            try:
                # Jellyfin requires the readiness probe to be the same GET as
                # its production fixture, before the credential-bearing POST.
                with urllib.request.urlopen(urllib.request.Request(base + "/Startup/User", method="GET"), timeout=10) as response:
                    if response.status == 200: break
            except urllib.error.HTTPError: pass
            if time.monotonic() >= end: raise Failure("Jellyfin Startup/User did not become ready")
            time.sleep(1)
        call("/Startup/User", {"Name": ADMIN_USER, "Password": self.admin_password})
        call("/Startup/RemoteAccess", {"EnableRemoteAccess": True, "EnableAutomaticPortMapping": False})
        call("/Startup/Complete")
        auth = f'MediaBrowser Client="NZBDAV E2E", Device="NZBDAV E2E", DeviceId="{self.project}", Version="1.0"'
        authenticated = call("/Users/AuthenticateByName", {"Username": ADMIN_USER, "Pw": self.admin_password}, {"X-Emby-Authorization": auth}, 200)
        self.jellyfin_token = str(authenticated.get("AccessToken", ""))
        if not self.jellyfin_token: raise Failure("Jellyfin did not issue an admin access token")
        self.redactor.add(self.jellyfin_token)
        self.record("jellyfin-initialized", authenticated=True)

    def jellyfin(self, path: str, method: str = "GET", body: Any | None = None, expected: int = 200) -> Any:
        data = None if body is None else json.dumps(body).encode()
        request = urllib.request.Request(f"http://127.0.0.1:{self.ports['jellyfin']}" + path, data=data, headers={"X-Emby-Token": self.jellyfin_token, "Content-Type": "application/json"}, method=method)
        try:
            with urllib.request.urlopen(request, timeout=90) as response:
                raw = response.read(4 * 1024 * 1024).decode("utf-8", "replace")
                if response.status != expected: raise Failure(f"Jellyfin {path}: {response.status}")
                return json.loads(raw) if raw else None
        except urllib.error.HTTPError as error: raise Failure(f"Jellyfin {path}: {error.code}") from error

    @staticmethod
    def _diagnostic_value(value: Any) -> Any:
        """Bound diagnostic output to contract metadata, never field secrets."""
        sensitive = ("pass", "token", "key", "grant", "secret", "authorization", "cookie")
        if isinstance(value, dict):
            # Arr/Prowlarr encode credential fields as {name, value}; redact
            # their values even when an upstream GET unexpectedly does not.
            field_name = value.get("name")
            if isinstance(field_name, str) and any(word in field_name.lower() for word in sensitive):
                return {str(k): "[REDACTED]" if str(k).lower() == "value" else Harness._diagnostic_value(v) for k, v in value.items()}
            return {str(k): "[REDACTED]" if any(word in str(k).lower() for word in sensitive) else Harness._diagnostic_value(v) for k, v in value.items()}
        if isinstance(value, list):
            return [Harness._diagnostic_value(item) for item in value]
        if isinstance(value, str):
            return value[:64 * 1024] + ("…[TRUNCATED]" if len(value) > 64 * 1024 else "")
        return value

    def _diagnostic_response(self, status: int, raw: str) -> dict[str, Any]:
        """JSON if possible, otherwise a bounded UTF-8 replacement excerpt."""
        try:
            body: Any = json.loads(raw)
        except json.JSONDecodeError:
            body = raw[:64 * 1024] + ("…[TRUNCATED]" if len(raw) > 64 * 1024 else "")
        return self.redactor.redact({"status": status, "body": self._diagnostic_value(body)})

    def service_local_snapshot(self, service: str) -> dict[str, Any]:
        """Receive only the fixed safe projection made inside a Servarr container.

        The product-generated credential is read and used entirely by the
        service-local shell process.  In particular, its value, config, and
        authenticated HTTP exchange never cross the Compose exec boundary.
        """
        if service not in {"sonarr", "radarr", "prowlarr"}:
            raise Failure(f"unsupported local verification service: {service}")
        script = self._local_verifier_script(service)
        result = self.compose_run(["exec", "-T", service, "sh", "-ec", script], timeout=60, check=False)
        if result.returncode:
            raise Failure(f"{service} service-local verification failed")
        return self._parse_local_snapshot(service, result.stdout)

    @staticmethod
    def _local_verifier_script(service: str) -> str:
        # stderr is deliberately closed before parsing config.xml so neither a
        # command diagnostic nor curl can carry credential-bearing text out of
        # the container. jq constructs the sole stdout value from an allowlist.
        name = {"sonarr": "NZBDAV Sonarr", "radarr": "NZBDAV Radarr"}.get(service)
        if name:
            root = "/data/completed-downloads/tv" if service == "sonarr" else "/data/completed-downloads/movies"
            return f'''exec 2>/dev/null
credential=$(sed -n 's:.*<ApiKey>\\([^<]*\\)</ApiKey>.*:\\1:p' /config/config.xml | head -n 1)
[ -n "$credential" ]
get() {{ curl -fsS --max-time 20 -H "X-Api-Key: $credential" "http://127.0.0.1:{8989 if service == "sonarr" else 7878}/api/v3/$1"; }}
clients=$(get downloadclient | jq '[.[] | select(.name == "{name}") | {{id: .id, name: .name, implementation: (.implementation // .implementationName // ""), configContract: (.configContract // ""), enable: (.enable == true)}}]')
roots=$(get rootfolder | jq '[.[] | select(.path == "{root}") | {{id: .id, path: .path}}]')
tests=$(curl -fsS --max-time 20 -X POST -H "X-Api-Key: $credential" "http://127.0.0.1:{8989 if service == "sonarr" else 7878}/api/v3/downloadclient/testall" | jq '[.[]? | {{id: .id, isValid: (.isValid == true)}}]')
jq -cn --argjson clients "$clients" --argjson roots "$roots" --argjson tests "$tests" '{{download_clients: $clients, root_folders: $roots, download_client_testall: $tests}}'
'''
        return '''exec 2>/dev/null
credential=$(sed -n 's:.*<ApiKey>\\([^<]*\\)</ApiKey>.*:\\1:p' /config/config.xml | head -n 1)
[ -n "$credential" ]
get() { curl -fsS --max-time 20 -H "X-Api-Key: $credential" "http://127.0.0.1:9696/api/v1/$1"; }
indexers=$(get indexer | jq '[.[] | select(.name == "NZBDAV E2E Newznab") | {id: .id, name: .name, implementation: (.implementation // .implementationName // ""), configContract: (.configContract // ""), enable: (.enable == true)}]')
applications=$(get applications | jq '[.[] | select(.name == "NZBDAV Sonarr" or .name == "NZBDAV Radarr") | {id: .id, name: .name, implementation: (.implementation // .implementationName // ""), configContract: (.configContract // ""), enable: (.enable == true), syncLevel: .syncLevel}]')
indexer_tests=$(curl -fsS --max-time 20 -X POST -H "X-Api-Key: $credential" "http://127.0.0.1:9696/api/v1/indexer/testall" | jq '[.[]? | {id: .id, isValid: (.isValid == true)}]')
application_tests=$(curl -fsS --max-time 20 -X POST -H "X-Api-Key: $credential" "http://127.0.0.1:9696/api/v1/applications/testall" | jq '[.[]? | {id: .id, isValid: (.isValid == true)}]')
jq -cn --argjson indexers "$indexers" --argjson applications "$applications" --argjson indexer_tests "$indexer_tests" --argjson application_tests "$application_tests" '{indexers: $indexers, applications: $applications, indexer_testall: $indexer_tests, applications_testall: $application_tests}'
'''

    @staticmethod
    def _parse_local_snapshot(service: str, raw: str) -> dict[str, Any]:
        """Accept only the small, non-secret local-verifier schema."""
        if service not in {"sonarr", "radarr", "prowlarr"}:
            raise Failure("unsupported local verification service")
        if len(raw.encode("utf-8", "replace")) > 64 * 1024:
            raise Failure(f"{service} local verification exceeded safe output limit")
        try:
            value = json.loads(raw)
        except json.JSONDecodeError as error:
            raise Failure(f"{service} local verification did not return JSON") from error
        if not isinstance(value, dict):
            raise Failure(f"{service} local verification was not an object")
        sensitive = ("pass", "token", "key", "grant", "secret", "authorization", "cookie", "credential")
        allowed = ({"download_clients", "root_folders", "download_client_testall"}
                   if service in {"sonarr", "radarr"}
                   else {"indexers", "applications", "indexer_testall", "applications_testall"})
        if set(value) != allowed or any(not isinstance(items, list) for items in value.values()):
            raise Failure(f"{service} local verification returned an unsafe shape")

        def safe_text(field: Any, key: str) -> None:
            # The verifier has no URL or credential fields. Limit each textual
            # field independently so names and Servarr contracts remain useful
            # diagnostics while secret-shaped payloads never reach evidence.
            limits = {"name": 128, "implementation": 64, "configContract": 128, "path": 256}
            if not isinstance(field, str) or not field or len(field) > limits[key]:
                raise Failure(f"{service} local verification returned unsafe text")
            if any(ord(char) < 32 or ord(char) == 127 for char in field):
                raise Failure(f"{service} local verification returned unsafe text")
            compact = field.strip()
            if (re.fullmatch(r"[0-9A-Fa-f]{32}|[0-9A-Fa-f]{40}|[0-9A-Fa-f]{64}", compact)
                    or re.search(r"(?i)\bbearer\s+\S+|(?:[?&]|\b)(?:api[_-]?key|access[_-]?token|token|password|credential)=[^&\s]+", compact)):
                raise Failure(f"{service} local verification attempted secret output")
            # Long, high-entropy base64/base64url strings are credentials in
            # this projection. Do not apply this rule to normal names/paths.
            if key != "path" and len(compact) >= 32 and re.fullmatch(r"[A-Za-z0-9_+/=-]+", compact):
                alphabet = len(set(compact.rstrip("=")))
                if alphabet >= 16:
                    raise Failure(f"{service} local verification attempted secret output")

        def check(item: Any, keys: set[str]) -> None:
            if not isinstance(item, dict) or set(item) != keys:
                raise Failure(f"{service} local verification returned an unsafe resource")
            for key, field in item.items():
                if any(word in key.lower() for word in sensitive):
                    raise Failure(f"{service} local verification attempted secret output")
                if key == "id":
                    if isinstance(field, bool) or not isinstance(field, int) or not 1 <= field <= 2_147_483_647:
                        raise Failure(f"{service} local verification returned invalid ID")
                elif key == "isValid" or key == "enable":
                    if not isinstance(field, bool):
                        raise Failure(f"{service} local verification returned invalid status")
                elif key == "syncLevel":
                    # The only supported application synchronization mode is
                    # the full production indexer synchronization contract.
                    if field != "fullSync":
                        raise Failure(f"{service} local verification returned invalid sync level")
                elif key in {"name", "implementation", "configContract", "path"}:
                    safe_text(field, key)

        resource_keys = {"id", "name", "implementation", "configContract", "enable"}
        application_resource_keys = resource_keys | {"syncLevel"}
        for field in ("download_clients", "indexers"):
            for item in value.get(field, []): check(item, resource_keys)
        for item in value.get("applications", []): check(item, application_resource_keys)
        for item in value.get("root_folders", []): check(item, {"id", "path"})
        for field in ("download_client_testall", "indexer_testall", "applications_testall"):
            for item in value.get(field, []): check(item, {"id", "isValid"})
        return value

    def _jellyfin_diagnostic_request(self, path: str, method: str = "GET", body: Any | None = None) -> dict[str, Any]:
        data = json.dumps(body).encode() if body is not None else None
        request = urllib.request.Request(
            f"http://127.0.0.1:{self.ports['jellyfin']}{path}", data=data,
            headers={"X-Emby-Token": self.jellyfin_token, "Accept": "application/json", "Content-Type": "application/json"}, method=method)
        try:
            with urllib.request.urlopen(request, timeout=30) as response:
                return self._diagnostic_response(response.status, response.read(64 * 1024 + 1).decode("utf-8", "replace"))
        except urllib.error.HTTPError as error:
            return self._diagnostic_response(error.code, error.read(64 * 1024 + 1).decode("utf-8", "replace"))

    def retain_live_diagnostics(self) -> None:
        """Failure-only, redacted proof of the pinned service contracts."""
        data: dict[str, Any] = {}
        # Backend logs include operation/status metadata but can also contain
        # upstream text. Retain only a fixed, redacted diagnostic tail.
        try:
            logs = self.compose_run(["logs", "--no-color", "nzbdav"], timeout=30, check=False).stdout
            data["nzbdav_log_tail"] = self.redactor.redact(logs[-16 * 1024:])
        except Exception as error:
            data["nzbdav_log_tail"] = {"error": type(error).__name__}
        try:
            base = f"http://127.0.0.1:{self.ports['nzbdav']}"
            status, raw, _ = self.http_raw(base + "/api/setup/status")
            data["setup_status"] = self._diagnostic_response(status, raw)
        except Exception as error:
            data["setup_status"] = {"error": type(error).__name__}
        try:
            data["prowlarr"] = self.service_local_snapshot("prowlarr")
        except Exception as error:
            data["prowlarr"] = {"error": type(error).__name__}
        arr: dict[str, Any] = {}
        for service in ("sonarr", "radarr"):
            try:
                arr[service] = self.service_local_snapshot(service)
            except Exception as error:
                arr[service] = {"error": type(error).__name__}
        data["arr"] = arr
        try:
            plugin_route = "/Plugins/a1b2c3d4-e5f6-7890-abcd-ef1234567890/Configuration"
            data["jellyfin"] = {
                "plugins": self._jellyfin_diagnostic_request("/Plugins"),
                "plugin_configuration": self._jellyfin_diagnostic_request(plugin_route),
                "virtual_folders": self._jellyfin_diagnostic_request("/Library/VirtualFolders"),
                "scheduled_tasks": self._jellyfin_diagnostic_request("/ScheduledTasks"),
            }
        except Exception as error:
            data["jellyfin"] = {"error": type(error).__name__}
        self.record("live-diagnostics", **data)

    def status(self) -> dict[str, Any]:
        value = self.browser_json("/api/setup/status")
        if not isinstance(value, dict): raise Failure("public setup status was not an object")
        return value

    def arr_snapshot(self) -> dict[str, Any]:
        sonarr = self.service_local_snapshot("sonarr")
        radarr = self.service_local_snapshot("radarr")
        prowlarr = self.service_local_snapshot("prowlarr")
        return {"sonarr_download_clients": sonarr["download_clients"], "sonarr_root_folders": sonarr["root_folders"],
                "sonarr_download_client_testall": sonarr["download_client_testall"],
                "radarr_download_clients": radarr["download_clients"], "radarr_root_folders": radarr["root_folders"],
                "radarr_download_client_testall": radarr["download_client_testall"],
                "prowlarr_indexers": prowlarr["indexers"], "prowlarr_applications": prowlarr["applications"],
                "prowlarr_indexer_testall": prowlarr["indexer_testall"], "prowlarr_applications_testall": prowlarr["applications_testall"]}

    def snapshot(self, label: str) -> dict[str, Any]:
        libraries = self.jellyfin("/Library/VirtualFolders")
        if not isinstance(libraries, list): raise Failure("Jellyfin virtual folders response was not a list")
        wanted = sorted(({"id": item.get("ItemId") or item.get("Id"), "name": item.get("Name"), "collectionType": item.get("CollectionType"), "locations": item.get("Locations")} for item in libraries if isinstance(item, dict) and item.get("Name") in {"NZBDAV TV", "NZBDAV Movies"}), key=lambda x: (str(x["name"]), str(x["id"])))
        plugin = self.jellyfin("/Plugins/a1b2c3d4-e5f6-7890-abcd-ef1234567890/Configuration")
        snapshot = {"libraries": wanted, "managed_library_count": len(wanted), "total_library_count": len(libraries), "plugin_config_sha256": hashlib.sha256(json.dumps(plugin, sort_keys=True, separators=(",", ":")).encode()).hexdigest(), **self.arr_snapshot()}
        self.record(label, **snapshot)
        return snapshot

    @staticmethod
    def library_ids(snapshot: dict[str, Any]) -> dict[str, Any]:
        libraries = snapshot["libraries"]
        expected = {
            "NZBDAV Movies": ("movies", ["/media/nzbdav/movies"]),
            # Jellyfin's pinned API serializes CollectionType as lowercase.
            "NZBDAV TV": ("tvshows", ["/media/nzbdav/tv"]),
        }
        if not isinstance(libraries, list) or len(libraries) != len(expected):
            raise Failure("expected exactly one NZBDAV Movies and one NZBDAV TV library")
        seen: set[str] = set()
        ids: dict[str, Any] = {}
        for entry in libraries:
            if not isinstance(entry, dict) or set(entry) != {"id", "name", "collectionType", "locations"}:
                raise Failure("Jellyfin managed library snapshot had an unsafe shape")
            name = entry["name"]
            if name not in expected or name in seen:
                raise Failure("expected exactly one NZBDAV Movies and one NZBDAV TV library")
            collection_type, locations = expected[name]
            if entry["collectionType"] != collection_type or entry["locations"] != locations:
                raise Failure(f"Jellyfin {name} library contract did not match collection type and location")
            seen.add(name); ids[name] = entry["id"]
        if any(not isinstance(value, str) or not value for value in ids.values()) or len(set(ids.values())) != 2:
            raise Failure("managed Jellyfin libraries did not have stable unique IDs")
        return ids

    @staticmethod
    def assert_arr_counts(snapshot: dict[str, Any]) -> None:
        expected = {"sonarr_download_clients": 1, "sonarr_root_folders": 1,
                    "radarr_download_clients": 1, "radarr_root_folders": 1,
                    "prowlarr_indexers": 1, "prowlarr_applications": 2}
        for field, count in expected.items():
            resources = snapshot[field]
            if len(resources) != count or len({item["id"] for item in resources}) != count:
                raise Failure(f"expected exactly {count} managed {field} with unique IDs")

        def assert_exact_testall(field: str, expected_ids: set[int]) -> None:
            tests = snapshot[field]
            actual_ids = [item["id"] for item in tests]
            # Every endpoint returns precisely one outcome for each managed
            # resource. Accepting any successful row would hide extra,
            # duplicate, contradictory, or failed testall results.
            if (len(tests) != len(expected_ids) or len(set(actual_ids)) != len(actual_ids)
                    or set(actual_ids) != expected_ids or not all(item["isValid"] for item in tests)):
                raise Failure(f"{field} did not return exactly one valid result per managed resource")

        for service in ("sonarr", "radarr"):
            client = snapshot[f"{service}_download_clients"][0]
            root = snapshot[f"{service}_root_folders"][0]
            if (client["implementation"].lower() != "sabnzbd" or client["configContract"] != "SabnzbdSettings"
                    or not client["enable"] or root["path"] != f"/data/completed-downloads/{'tv' if service == 'sonarr' else 'movies'}"):
                raise Failure(f"{service} local verification did not prove healthy managed SAB/root")
            assert_exact_testall(f"{service}_download_client_testall", {client["id"]})
        indexer = snapshot["prowlarr_indexers"][0]
        apps = snapshot["prowlarr_applications"]
        expected_indexer = ("NZBDAV E2E Newznab", "Newznab", "NewznabSettings")
        expected_apps = {
            "NZBDAV Sonarr": ("Sonarr", "SonarrSettings", "fullSync"),
            "NZBDAV Radarr": ("Radarr", "RadarrSettings", "fullSync"),
        }
        if ((indexer["name"], indexer["implementation"], indexer["configContract"]) != expected_indexer
                or {(app["name"], app["implementation"], app["configContract"], app["syncLevel"]) for app in apps}
                   != {(name, *contract) for name, contract in expected_apps.items()}
                or not indexer["enable"] or not all(item["enable"] for item in apps)):
            raise Failure("Prowlarr local verification did not prove pinned managed indexer/apps")
        assert_exact_testall("prowlarr_indexer_testall", {indexer["id"]})
        assert_exact_testall("prowlarr_applications_testall", {app["id"] for app in apps})

    @staticmethod
    def managed_identity_map(snapshot: dict[str, Any]) -> dict[str, Any]:
        """Return the complete stable identity set after validating its contract."""
        libraries = Harness.library_ids(snapshot)
        Harness.assert_arr_counts(snapshot)
        return {
            "jellyfin_libraries": libraries,
            "sonarr": {"download_client": snapshot["sonarr_download_clients"][0]["id"],
                       "root_folder": snapshot["sonarr_root_folders"][0]["id"]},
            "radarr": {"download_client": snapshot["radarr_download_clients"][0]["id"],
                       "root_folder": snapshot["radarr_root_folders"][0]["id"]},
            "prowlarr": {
                "indexer": snapshot["prowlarr_indexers"][0]["id"],
                "applications": {app["name"]: app["id"] for app in snapshot["prowlarr_applications"]},
            },
        }

    @staticmethod
    def assert_managed_identity_equal(expected: dict[str, Any], actual: dict[str, Any], context: str) -> None:
        if actual != expected:
            raise Failure(f"{context} changed complete managed identity: "
                          + json.dumps({"expected": expected, "actual": actual}, sort_keys=True))

    def assert_ready(self) -> None:
        status = self.status()
        ready = {entry.get("name"): entry.get("ready") for entry in status.get("services", [])}
        if not status.get("completed") or status.get("repairRequired") or any(ready.get(name) is not True for name in SERVICES):
            raise Failure("public status did not report Ready: " + json.dumps(self.redactor.redact(status)))

    def assert_fake_calls(self) -> None:
        # A successful setup is insufficient: inspect only sanitized fake
        # process output by proving protocol calls via its bounded audit API.
        audit = self.fake_audit()
        if audit: self.record("fake-audit", calls=audit)
        # The fake's strict implementation rejects all incorrect credentials
        # and order; product setup must succeed through both endpoints. Its
        # call counter is provided by a small test-only endpoint below.
        counts = self.fake_counts()
        if (counts.get("nntp-user-ok", 0) < 1 or counts.get("nntp-pass-ok", 0) < 1
                or counts.get("newznab-caps-unauthenticated-ok", 0) < 1):
            raise Failure("configured credentials did not reach both strict fakes: " + repr(counts))
        self.record("strict-fakes-verified", counts=counts)

    def fake_indexer_url(self) -> str:
        try:
            address = ipaddress.ip_address(self.fake_stack_ip)
        except ValueError as error:
            raise Failure("fake did not receive a private IPv4 stack address") from error
        if address.version != 4 or not address.is_private or address.is_loopback or address.is_multicast:
            raise Failure("fake did not receive a private IPv4 stack address")
        return f"http://{address}:{self.fake_newznab_port}"

    def discover_fake_stack_ip(self, stack_network: str) -> None:
        template = f'{{{{with index .NetworkSettings.Networks "{stack_network}"}}}}{{{{.IPAddress}}}}{{{{end}}}}'
        self.fake_stack_ip = self.run(
            ["docker", "inspect", "--format", template, self.fake_name], timeout=30).stdout.strip()
        _ = self.fake_indexer_url()

    def fake_counts(self) -> dict[str, int]:
        # The fake audit endpoint is reachable only over the private Docker
        # network and returns count metadata, never request values.
        return self._fake_counts_over_network()

    def fake_audit_command(self) -> list[str]:
        # Python is intentionally never executed in the NZBDAV runtime image.
        # Busybox wget is supplied by the disposable python:alpine fake image.
        return ["docker", "exec", self.fake_name, "wget", "-qO-", f"http://127.0.0.1:{self.fake_newznab_port}/audit"]

    def _fake_counts_over_network(self) -> dict[str, int]:
        raw = self.run(self.fake_audit_command(), timeout=30).stdout
        try:
            value = json.loads(raw)
            return {str(k): int(v) for k, v in value.items()}
        except (json.JSONDecodeError, ValueError): raise Failure("fake audit returned invalid bounded summary")

    def start_stack(self, env: dict[str, str]) -> None:
        # Compose Desktop can evaluate a dependent service before its
        # predecessor's start-period health state settles. Start each directed
        # dependency tier, then use the harness's explicit bounded inspector.
        for services in (("sonarr", "radarr", "prowlarr"), ("nzbdav",), ("jellyfin",)):
            command = self.compose + ["up", "--build", "--detach"]
            if len(services) == 1:
                command.append("--no-deps")
            command.extend(services)
            result = self.run(command, timeout=900, check=False, env=env)
            if result.returncode:
                raise Failure("compose up failed: " + str(self.redactor.redact(result.stdout))[-30000:])
            self.wait_services(services)

    def discover_published_ports(self) -> None:
        discovered: dict[str, int] = {}
        for service in SERVICES:
            container_port = SERVICE_CONTAINER_PORTS[service]
            raw = self.compose_run(["port", service, str(container_port)], timeout=30).stdout.strip().splitlines()
            if len(raw) != 1:
                raise Failure(f"could not discover one published port for {service}")
            match = re.fullmatch(r"127\.0\.0\.1:([1-9][0-9]{0,4})", raw[0].strip())
            if not match:
                raise Failure(f"could not discover loopback port for {service}")
            port = int(match.group(1))
            if port > 65535:
                raise Failure(f"discovered out-of-range port for {service}")
            discovered[service] = port
        self.ports = discovered
        # self.ports is replaced above, so update retained evidence rather
        # than leaving it pointing at the initial all-zero allocation request.
        self.evidence["ports"] = dict(discovered)

    def execute(self) -> None:
        self.start_fakes()
        normalized = self.compose_run(["config", "--format", "json"], timeout=60).stdout
        try:
            topology = assert_normalized_compose_topology(json.loads(normalized))
        except json.JSONDecodeError as error:
            raise Failure("normalized Compose config did not return JSON") from error
        self.record("compose-topology-validated", **topology)
        env = os.environ.copy(); env.update({"BIND_ADDRESS": "127.0.0.1", "NZBDAV_PORT": str(self.ports["nzbdav"]), "JELLYFIN_PORT": str(self.ports["jellyfin"]), "JELLYFIN_PUBLIC_PORT": "8096", "SONARR_PORT": str(self.ports["sonarr"]), "RADARR_PORT": str(self.ports["radarr"]), "PROWLARR_PORT": str(self.ports["prowlarr"]), "NZBDAV_MASTER_KEY": ""})
        self.start_stack(env)
        self.discover_published_ports()
        self.record("compose-up")
        stack_network = self.project + "_full_stack"
        self.run(["docker", "network", "connect", "--alias", "e2e-fakes", stack_network, self.fake_name], timeout=30)
        self.discover_fake_stack_ip(stack_network)
        self.wait_healthy()
        self.bootstrap_jellyfin()
        # First unauthenticated public form: handoff creates the actual
        # frontend session and server-side setup handle.
        self.action({"action": "handoff", "username": ADMIN_USER, "password": self.admin_password}, self.initial_csrf())
        self.action({"action": "add-provider", "provider-host": "e2e-fakes", "provider-port": str(self.fake_nntp_port), "provider-user": self.nntp_user, "provider-pass": self.nntp_password, "provider-max": "1", "provider-type": "1"})
        self.action({"action": "add-indexer", "indexer-name": "NZBDAV E2E Newznab", "indexer-url": self.fake_indexer_url(), "indexer-apikey": self.indexer_key, "indexer-private-network": "on"})
        configured_status = self.action({"action": "configure"}, timeout=600)
        self.record("configured-through-browser", completed=configured_status.get("setupStatus", {}).get("completed") if isinstance(configured_status, dict) else None)
        # GET status deliberately does not perform external checks. Cross the
        # browser's explicit verification boundary before treating the newly
        # completed marker as Ready.
        self.action({"action": "verify-services"}, timeout=240)
        self.assert_ready(); self.assert_fake_calls()
        before = self.snapshot("before-reconcile")
        before_identities = self.managed_identity_map(before)
        self.record("canonical-managed-identities", identities=before_identities)
        # Healthy reconciliation is supported through the exact browser action;
        # it must be a no-op before any repair grant is requested.
        self.action({"action": "verify-services"}, timeout=240)
        self.assert_ready(); after_verify = self.snapshot("after-healthy-reconcile")
        if after_verify != before: raise Failure("healthy reconciliation mutated managed resources")
        self.managed_identity_map(after_verify)
        # Explicit, controlled public Jellyfin drift: delete one managed
        # library, then latch repair using the browser verification action.
        victim = "NZBDAV TV"; victim_id = before_identities["jellyfin_libraries"].get(victim)
        if not victim_id: raise Failure("cannot select managed library for controlled drift")
        self.jellyfin("/Library/VirtualFolders?name=" + urllib.parse.quote(victim), "DELETE", expected=204)
        drift = self.snapshot("controlled-drift")
        if drift["managed_library_count"] != 1: raise Failure("controlled drift did not remove exactly one library")
        self.action({"action": "verify-services"}, timeout=240)
        latched = self.status()
        if not latched.get("completed") or not latched.get("repairRequired"): raise Failure("live verification did not latch controlled repair")
        self.record("repair-latched", repair_required=True)
        # Repair is issued only after real drift and only by the public form.
        self.action({"action": "repair-handoff", "username": ADMIN_USER, "password": self.admin_password}, timeout=120)
        repaired = self.action({"action": "repair"}, timeout=600)
        self.record("repair-through-browser", response_shape=isinstance(repaired, dict))
        self.assert_ready(); after_repair = self.snapshot("after-repair")
        after_repair_identities = self.managed_identity_map(after_repair)
        self.record("repaired-managed-identities", identities=after_repair_identities)
        self.assert_managed_identity_equal(before_identities, after_repair_identities, "repair")
        self.restart_and_wait_for_public_frontend(); self.assert_ready()
        after_restart = self.snapshot("after-restart")
        self.managed_identity_map(after_restart)
        if after_restart != after_repair: raise Failure("restart changed repaired managed resources")
        self.record("passed", elapsed_seconds=round(time.monotonic() - self.started, 1))

    def labeled_ids(self, kind: str) -> list[str]:
        command = {"container": ["docker", "ps", "-aq"], "volume": ["docker", "volume", "ls", "-q"], "network": ["docker", "network", "ls", "-q"]}[kind]
        # Both labels are ours: Compose resources get the project label and
        # the disposable fake gets both that label and the harness label.
        result = self.run(command + ["--filter", f"label=com.docker.compose.project={self.project}"], timeout=30, check=False)
        if result.returncode:
            self.cleanup_errors.append(f"list {kind} returned {result.returncode}")
            return []
        return [line for line in result.stdout.splitlines() if line]

    def cleanup_command(self, description: str, command: list[str], timeout: int) -> None:
        try:
            result = self.run(command, timeout=timeout, check=False)
            if result.returncode:
                self.cleanup_errors.append(f"{description} returned {result.returncode}")
        except Exception as error:
            self.cleanup_errors.append(f"{description}: {type(error).__name__}")

    def cleanup(self) -> None:
        if self.keep_project:
            self.evidence["cleanup"] = {"retained_project": self.project, "errors": []}
            self._write_evidence()
            return
        # Each step is independent: a timeout or nonzero return from one must
        # not skip later cleanup. IDs are exact Docker IDs captured at create
        # time; labels catch Compose resources not explicitly captured.
        self.cleanup_command("compose down", self.compose + ["down", "--volumes", "--remove-orphans"], 180)
        self.cleanup_command("stop fake", ["docker", "stop", "-t", "5", self.fake_name], 15)
        self.cleanup_command("remove fake", ["docker", "rm", "-f", self.fake_name], 30)
        self.cleanup_command("remove fake network", ["docker", "network", "rm", self.fake_network], 30)
        for kind, remove in (("container", ["docker", "rm", "-f"]), ("volume", ["docker", "volume", "rm", "-f"]), ("network", ["docker", "network", "rm"])):
            # Verify exact owned IDs before fallback removal. The explicit fake
            # cleanup above commonly already removed them; do not turn that
            # successful cleanup into a spurious nonzero remove result.
            existing_owned = []
            for ident in self.owned_ids[kind]:
                inspected = self.run(["docker", "inspect", ident], timeout=30, check=False)
                if inspected.returncode == 0: existing_owned.append(ident)
            ids = sorted(set(self.labeled_ids(kind)) | set(existing_owned))
            if ids: self.cleanup_command(f"remove {kind}", remove + ids, 90)
            # Query after removals by exact project label, then report any
            # remaining exact owned IDs too (for a removed/mislabelled fake).
            leaked = self.labeled_ids(kind)
            existing_owned = []
            for ident in self.owned_ids[kind]:
                inspected = self.run(["docker", "inspect", ident], timeout=30, check=False)
                if inspected.returncode == 0: existing_owned.append(ident)
            remaining = sorted(set(leaked) | set(existing_owned))
            if remaining: self.cleanup_errors.append(f"leaked owned {kind}s: {remaining}")
        self.evidence["cleanup"] = {"project_resources_remaining": not not self.cleanup_errors, "errors": self.cleanup_errors}
        try: self._write_evidence()
        except Exception as error: self.cleanup_errors.append("evidence: " + type(error).__name__)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--keep-artifacts", action="store_true", help="print sanitized artifact directory on success too")
    parser.add_argument("--keep-project", action="store_true", help="retain this labeled project for failure diagnosis; manually run its Compose down afterward")
    args = parser.parse_args()
    if not COMPOSE_FILE.is_file(): raise SystemExit("run from the repository containing docker-compose.full-stack.yml")
    harness = Harness(args.keep_artifacts)
    harness.keep_project = args.keep_project
    signal.signal(signal.SIGINT, lambda *_: (_ for _ in ()).throw(KeyboardInterrupt()))
    signal.signal(signal.SIGTERM, lambda *_: (_ for _ in ()).throw(KeyboardInterrupt()))
    passed = False
    try:
        harness.execute(); passed = True
    except BaseException as error:
        # Diagnostics execute only after a failure, before project-label cleanup.
        # A diagnostic failure itself is retained as safe type/status metadata.
        try:
            harness.retain_live_diagnostics()
        except BaseException as diagnostic_error:
            harness.record("live-diagnostics", error=type(diagnostic_error).__name__, detail=str(diagnostic_error)[:500])
        # Artifacts and terminal failure evidence retain only redacted fields;
        # exception traces can contain server response text, so never emit one.
        harness.record("failed", exact_phase=harness.phase, error=type(error).__name__, detail=str(error))
    finally:
        harness.cleanup()
    if harness.cleanup_errors:
        print("full-stack onboarding E2E: FAIL cleanup=" + "; ".join(harness.cleanup_errors), file=sys.stderr); passed = False
    print("sanitized artifacts:", harness.artifacts, file=sys.stderr if not passed else sys.stdout)
    print("full-stack onboarding E2E:", "PASS" if passed else "FAIL phase=" + harness.phase, file=sys.stdout if passed else sys.stderr)
    return 0 if passed else 1

if __name__ == "__main__": raise SystemExit(main())

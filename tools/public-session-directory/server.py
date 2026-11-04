#!/usr/bin/env python3
"""Small, dependency-free directory for player-hosted Game Builder sessions.

Run behind an HTTPS reverse proxy for public use. The proxy must overwrite
X-Forwarded-For; pass --trusted-proxy with the proxy's peer IP to use it.
"""

import argparse
import hashlib
import hmac
import base64
import ipaddress
import json
import os
import secrets
import socket
import threading
import time
from collections import deque
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import unquote, urlsplit

MAX_BODY_BYTES = 4096
MAX_NAME_BYTES = 96
MAX_CERTIFICATE_PEM_BYTES = 2048
MAX_CERTIFICATE_DER_BYTES = 1536
MAX_PLAYERS = 16
MAX_SESSIONS = 1000
SESSION_TTL_SECONDS = 300
PUBLIC_SESSION_TTL_SECONDS = 60
MIGRATION_STALE_SECONDS = 30
REGISTER_LIMIT = 8
REGISTER_WINDOW_SECONDS = 300
MAX_REGISTER_SOURCES = 4096


class DirectoryState:
    def __init__(self, trusted_proxy=None):
        self._lock = threading.Lock()
        self._sessions = {}
        self._registers = {}
        self._trusted_proxy = trusted_proxy

    def _purge_locked(self, now):
        expired = [key for key, value in self._sessions.items()
                   if now - value["Updated"] > SESSION_TTL_SECONDS]
        for key in expired:
            del self._sessions[key]
        stale = [key for key, timestamps in self._registers.items()
                 if not timestamps or now - timestamps[-1] > REGISTER_WINDOW_SECONDS]
        for key in stale:
            del self._registers[key]

    def register(self, body, address):
        name, count, capacity, port, address_family, certificate, is_public = validate_session(body)
        require_address_family(address, address_family)
        now = time.monotonic()
        with self._lock:
            self._purge_locked(now)
            if address not in self._registers and len(self._registers) >= MAX_REGISTER_SOURCES:
                return None, "rate-limit"
            timestamps = self._registers.setdefault(address, deque())
            while timestamps and now - timestamps[0] > REGISTER_WINDOW_SECONDS:
                timestamps.popleft()
            if len(timestamps) >= REGISTER_LIMIT:
                return None, "rate-limit"
            timestamps.append(now)
            if len(self._sessions) >= MAX_SESSIONS:
                return None, "full"
            session_id = secrets.token_urlsafe(18)
            token = secrets.token_urlsafe(32)
            migration_token = secrets.token_urlsafe(32)
            self._sessions[session_id] = {
                "SessionId": session_id,
                "Name": name,
                "Address": address,
                "Port": port,
                "PlayerCount": count,
                "MaxPlayers": capacity,
                "ServerCertificatePem": certificate,
                "AddressFamily": address_family,
                "IsPublic": is_public,
                "Generation": 1,
                "TokenHash": hashlib.sha256(token.encode("ascii")).digest(),
                "MigrationTokenHash": hashlib.sha256(migration_token.encode("ascii")).digest(),
                "Updated": now,
            }
            return {"SessionId": session_id, "Token": token,
                    "MigrationToken": migration_token, "Generation": 1}, None

    def heartbeat(self, session_id, token, body, address):
        name, count, capacity, port, address_family, certificate, is_public = validate_session(body)
        require_address_family(address, address_family)
        token_hash = hashlib.sha256(token.encode("utf-8")).digest()
        now = time.monotonic()
        with self._lock:
            self._purge_locked(now)
            session = self._sessions.get(session_id)
            if session is None or not hmac.compare_digest(session["TokenHash"], token_hash):
                return False
            if session["AddressFamily"] != address_family:
                raise ValueError("Session transport family must match its registration.")
            session.update({
                "Name": name,
                "Address": address,
                "Port": port,
                "PlayerCount": count,
                "MaxPlayers": capacity,
                "ServerCertificatePem": certificate,
                "IsPublic": is_public,
                "Updated": now,
            })
            return True

    def migration_info(self, session_id, token):
        token_hash = hashlib.sha256(token.encode("utf-8")).digest()
        with self._lock:
            self._purge_locked(time.monotonic())
            session = self._sessions.get(session_id)
            if session is None or not hmac.compare_digest(session["MigrationTokenHash"], token_hash):
                return None
            return {key: session[key] for key in (
                "SessionId", "Address", "Port", "ServerCertificatePem", "Name",
                "PlayerCount", "MaxPlayers", "Generation", "IsPublic", "AddressFamily")}

    def migrate(self, session_id, token, body, address):
        name, count, capacity, port, address_family, certificate, is_public = validate_session(body)
        require_address_family(address, address_family)
        token_hash = hashlib.sha256(token.encode("utf-8")).digest()
        now = time.monotonic()
        with self._lock:
            self._purge_locked(now)
            session = self._sessions.get(session_id)
            if session is None or not hmac.compare_digest(session["MigrationTokenHash"], token_hash):
                return None, "not-found"
            if now - session["Updated"] < MIGRATION_STALE_SECONDS:
                return None, "active"
            new_token = secrets.token_urlsafe(32)
            session.update({
                "Name": name,
                "Address": address,
                "Port": port,
                "PlayerCount": count,
                "MaxPlayers": capacity,
                "ServerCertificatePem": certificate,
                "AddressFamily": address_family,
                "IsPublic": is_public,
                "Generation": session["Generation"] + 1,
                "TokenHash": hashlib.sha256(new_token.encode("ascii")).digest(),
                "Updated": now,
            })
            return {"SessionId": session_id, "Token": new_token,
                    "MigrationToken": token, "Generation": session["Generation"]}, None

    def unregister(self, session_id, token):
        token_hash = hashlib.sha256(token.encode("utf-8")).digest()
        with self._lock:
            session = self._sessions.get(session_id)
            if session is None or not hmac.compare_digest(session["TokenHash"], token_hash):
                return False
            del self._sessions[session_id]
            return True

    def list_sessions(self):
        now = time.monotonic()
        with self._lock:
            self._purge_locked(now)
            values = [
                {key: value for key, value in session.items()
                 if key not in ("TokenHash", "MigrationTokenHash", "Updated", "AddressFamily")}
                for session in self._sessions.values()
                if session["IsPublic"] and now - session["Updated"] <= PUBLIC_SESSION_TTL_SECONDS
            ]
        values.sort(key=lambda value: (value["Name"].casefold(), value["SessionId"]))
        return {"Sessions": values}

    def request_address(self, handler):
        peer = normalize_ip(handler.client_address[0])
        if self._trusted_proxy is not None and peer == self._trusted_proxy:
            forwarded = handler.headers.get("X-Forwarded-For", "")
            if forwarded:
                candidate = normalize_ip(forwarded.split(",", 1)[0].strip())
                if candidate:
                    peer = candidate
        if not peer:
            raise ValueError("Could not determine the request IP address.")
        address = ipaddress.ip_address(peer)
        return str(address)


def normalize_ip(value):
    try:
        parsed = ipaddress.ip_address(value)
        if isinstance(parsed, ipaddress.IPv6Address) and parsed.ipv4_mapped:
            parsed = parsed.ipv4_mapped
        return str(parsed)
    except (ValueError, TypeError):
        return None

def require_address_family(address, expected_family):
    if ipaddress.ip_address(address).version != expected_family:
        raise ValueError("Host transport family must match the directory request IP family.")



def validate_session(body):
    if not isinstance(body, dict):
        raise ValueError("Expected a JSON object.")
    name = body.get("Name")
    if not isinstance(name, str):
        raise ValueError("Name must be text.")
    name = name.strip()
    if not name:
        name = "Game Builder game"
    try:
        name_bytes = name.encode("utf-8")
    except UnicodeEncodeError as error:
        raise ValueError("Name must be valid Unicode text.") from error
    if len(name_bytes) > MAX_NAME_BYTES:
        raise ValueError("Name exceeds 96 UTF-8 bytes.")
    if any(ord(character) < 32 or ord(character) == 127 for character in name):
        raise ValueError("Name contains control characters.")
    count = body.get("PlayerCount")
    capacity = body.get("MaxPlayers")
    port = body.get("Port")
    if any(isinstance(value, bool) or not isinstance(value, int)
           for value in (count, capacity, port)):
        raise ValueError("PlayerCount, MaxPlayers, and Port must be integers.")
    if not 1 <= capacity <= MAX_PLAYERS or not 1 <= count <= capacity:
        raise ValueError("Player count/capacity is outside the supported range.")
    if not 1 <= port <= 65535:
        raise ValueError("Port must be between 1 and 65535.")
    address_family = body.get("AddressFamily", 4)
    if isinstance(address_family, bool) or not isinstance(address_family, int) or address_family not in (4, 6):
        raise ValueError("AddressFamily must be 4 or 6.")
    is_public = body.get("IsPublic", True)
    if not isinstance(is_public, bool):
        raise ValueError("IsPublic must be a boolean.")
    certificate = body.get("ServerCertificatePem")
    if not isinstance(certificate, str):
        raise ValueError("ServerCertificatePem must be PEM text.")
    try:
        certificate_bytes = certificate.strip().encode("ascii")
    except UnicodeEncodeError as error:
        raise ValueError("ServerCertificatePem must contain ASCII PEM text.") from error
    begin = b"-----BEGIN CERTIFICATE-----"
    end = b"-----END CERTIFICATE-----"
    if len(certificate_bytes) > MAX_CERTIFICATE_PEM_BYTES:
        raise ValueError("ServerCertificatePem exceeds the supported size.")
    if not certificate_bytes.startswith(begin) or not certificate_bytes.endswith(end):
        raise ValueError("ServerCertificatePem must contain one certificate PEM block.")
    encoded_der = certificate_bytes[len(begin):-len(end)]
    try:
        der = base64.b64decode(b"".join(encoded_der.split()), validate=True)
    except ValueError as error:
        raise ValueError("ServerCertificatePem contains invalid base64.") from error
    if not der or len(der) > MAX_CERTIFICATE_DER_BYTES:
        raise ValueError("ServerCertificatePem contains an empty or oversized certificate.")
    return name, count, capacity, port, address_family, certificate_bytes.decode("ascii"), is_public


class DirectoryHandler(BaseHTTPRequestHandler):
    server_version = "GameBuilderSessionDirectory/1"
    state = None

    def do_GET(self):
        path = urlsplit(self.path).path
        if path == "/health":
            self.send_json(200, {"Status": "ok"})
        elif path == "/v1/sessions":
            self.send_json(200, self.state.list_sessions())
        elif path.endswith("/migration"):
            session_id = self.session_id_from_path("/migration")
            token = self.bearer_token()
            result = self.state.migration_info(session_id, token) if session_id and token else None
            if result is None:
                self.send_json(404, {"Error": "session not found"})
            else:
                self.send_json(200, result)
        else:
            self.send_json(404, {"Error": "not found"})

    def do_POST(self):
        path = urlsplit(self.path).path
        if path == "/v1/sessions":
            try:
                body = self.read_json()
                address = self.state.request_address(self)
                result, error = self.state.register(body, address)
            except ValueError as error:
                self.send_json(400, {"Error": str(error)})
                return
            if error == "rate-limit":
                self.send_json(429, {"Error": "registration rate limit exceeded"})
            elif error == "full":
                self.send_json(503, {"Error": "directory is full"})
            else:
                self.send_json(201, result)
            return
        session_id = self.session_id_from_path("/migrate")
        if session_id is None:
            self.send_json(404, {"Error": "not found"})
            return
        token = self.bearer_token()
        if not token:
            self.send_json(404, {"Error": "session not found"})
            return
        try:
            body = self.read_json()
            address = self.state.request_address(self)
            result, error = self.state.migrate(session_id, token, body, address)
        except ValueError as error:
            self.send_json(400, {"Error": str(error)})
            return
        if error == "not-found":
            self.send_json(404, {"Error": "session not found"})
        elif error == "active":
            self.send_json(409, {"Error": "session host heartbeat is too recent"})
        else:
            self.send_json(200, result)

    def do_PUT(self):
        session_id = self.session_id_from_path()
        if session_id is None:
            self.send_json(404, {"Error": "not found"})
            return
        try:
            body = self.read_json()
            address = self.state.request_address(self)
            token = self.bearer_token()
            updated = bool(token) and self.state.heartbeat(session_id, token, body, address)
        except ValueError as error:
            self.send_json(400, {"Error": str(error)})
            return
        if not updated:
            self.send_json(404, {"Error": "session not found"})
            return
        self.send_response(204)
        self.send_header("Content-Length", "0")
        self.end_headers()

    def do_DELETE(self):
        session_id = self.session_id_from_path()
        if session_id is None:
            self.send_json(404, {"Error": "not found"})
            return
        token = self.bearer_token()
        if not token or not self.state.unregister(session_id, token):
            self.send_json(404, {"Error": "session not found"})
            return
        self.send_response(204)
        self.send_header("Content-Length", "0")
        self.end_headers()

    def session_id_from_path(self, suffix=None):
        parts = urlsplit(self.path).path.split("/")
        expected = 5 if suffix else 4
        if len(parts) != expected or parts[:3] != ["", "v1", "sessions"]:
            return None
        if suffix and parts[4] != suffix[1:]:
            return None
        session_id = unquote(parts[3])
        if not session_id or "/" in session_id or "\\" in session_id:
            return None
        return session_id

    def bearer_token(self):
        header = self.headers.get("Authorization", "")
        if not header.startswith("Bearer "):
            return None
        token = header[7:].strip()
        if len(token) > 128:
            return None
        return token or None

    def read_json(self):
        if self.headers.get("Transfer-Encoding"):
            raise ValueError("Chunked request bodies are not supported.")
        try:
            length = int(self.headers.get("Content-Length", "-1"))
        except ValueError:
            length = -1
        if length < 0 or length > MAX_BODY_BYTES:
            raise ValueError("Invalid request body size.")
        raw = self.rfile.read(length)
        try:
            return json.loads(raw.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as error:
            raise ValueError("Request body must be valid UTF-8 JSON.") from error

    def send_json(self, status, value):
        encoded = json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(encoded)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(encoded)

    def log_message(self, format_string, *args):
        super().log_message(format_string, *args)


class DirectoryServer(ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = True


class IPv6DirectoryServer(DirectoryServer):
    address_family = socket.AF_INET6


def main():
    parser = argparse.ArgumentParser(description="Game Builder direct-host session directory")
    parser.add_argument("--bind", default=os.environ.get("GB_DIRECTORY_BIND", "127.0.0.1"))
    parser.add_argument("--port", type=int, default=int(os.environ.get("GB_DIRECTORY_PORT", "47800")))
    parser.add_argument("--trusted-proxy", default=os.environ.get("GB_DIRECTORY_TRUSTED_PROXY"))
    args = parser.parse_args()
    if not 1 <= args.port <= 65535:
        parser.error("--port must be between 1 and 65535")
    trusted_proxy = None
    if args.trusted_proxy:
        trusted_proxy = normalize_ip(args.trusted_proxy)
        if not trusted_proxy:
            parser.error("--trusted-proxy must be an IP address")
    handler = type("ConfiguredDirectoryHandler", (DirectoryHandler,), {
        "state": DirectoryState(trusted_proxy),
    })
    try:
        bind_address = ipaddress.ip_address(args.bind)
    except ValueError:
        bind_address = None
    server_class = IPv6DirectoryServer if bind_address is not None and bind_address.version == 6 else DirectoryServer
    server = server_class((args.bind, args.port), handler)
    bound_address = server.server_address
    display_address = "[{}]".format(bound_address[0]) if server.address_family == socket.AF_INET6 else bound_address[0]
    print("Game Builder session directory listening on {}:{}".format(display_address, bound_address[1]), flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == "__main__":
    main()

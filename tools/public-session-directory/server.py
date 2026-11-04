#!/usr/bin/env python3
"""Small, dependency-free directory for player-hosted Game Builder sessions.

Run behind an HTTPS reverse proxy for public use. The proxy must overwrite
X-Forwarded-For; pass --trusted-proxy with the proxy's peer IP to use it.
"""

import argparse
import hashlib
import hmac
import binascii
import base64
import ipaddress
import json
import os
import secrets
import selectors
import socket
import struct
import threading
import time
from collections import deque
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import unquote, urlsplit

RELAY_HEADER = struct.Struct("!4sB24sQ")
RELAY_MAGIC = b"GBR1"
RELAY_MAX_DATAGRAM = 65507
RELAY_MAX_PAYLOAD = 65507 - RELAY_HEADER.size
RELAY_CLIENT_IDLE_SECONDS = 60
RELAY_MAX_CLIENTS = 4096
RELAY_MAX_PER_SESSION = 16

MAX_BODY_BYTES = 32768
MAX_THUMBNAIL_BYTES = 16 * 1024
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
PROXY_V2_SIGNATURE = b"\r\n\r\n\x00\r\nQUIT\n"
MAX_PROXY_V2_PAYLOAD_BYTES = 4096
PROXY_V2_HEADER_TIMEOUT_SECONDS = 5


class DirectoryState:
    def __init__(self, trusted_proxy=None, relay_address=None, relay_port=None):
        self._lock = threading.Lock()
        self._sessions = {}
        self._registers = {}
        self._trusted_proxy = trusted_proxy
        self.relay_address = relay_address
        self.relay_port = relay_port

    def _relay_fields(self):
        if self.relay_address is None or self.relay_port is None:
            return {}
        return {"RelayAddress": self.relay_address, "RelayPort": self.relay_port}

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
        name, count, capacity, port, address_family, certificate, is_public, thumbnail = validate_session(body)
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
                "ThumbnailZippedJpegBase64": thumbnail,
                "AddressFamily": address_family,
                "IsPublic": is_public,
                "Generation": 1,
                "TokenHash": hashlib.sha256(token.encode("ascii")).digest(),
                "MigrationTokenHash": hashlib.sha256(migration_token.encode("ascii")).digest(),
                "Updated": now,
            }
            return {"SessionId": session_id, "Token": token,
                    "MigrationToken": migration_token, "Generation": 1,
                    **self._relay_fields()}, None

    def heartbeat(self, session_id, token, body, address):
        name, count, capacity, port, address_family, certificate, is_public, thumbnail = validate_session(body)
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
                "ThumbnailZippedJpegBase64": thumbnail,
                "IsPublic": is_public,
                "Updated": now,
            })
            return True
    def verify_relay_heartbeat(self, session_id, sequence, header, signature):
        now = time.monotonic()
        with self._lock:
            self._purge_locked(now)
            session = self._sessions.get(session_id)
            if session is None or len(signature) != hashlib.sha256().digest_size:
                return False
            expected = hmac.new(session["TokenHash"], header, hashlib.sha256).digest()
            if not hmac.compare_digest(expected, signature):
                return False
            if sequence <= session.get("RelaySequence", 0):
                return False
            session["RelaySequence"] = sequence
            session["RelayHeartbeat"] = now
            return True

    def relay_session_snapshot(self, session_id):
        with self._lock:
            self._purge_locked(time.monotonic())
            session = self._sessions.get(session_id)
            if session is None:
                return None
            return (session.get("RelayHeartbeat", 0), session["MaxPlayers"],
                    session["PlayerCount"])

    def migration_info(self, session_id, token):
        token_hash = hashlib.sha256(token.encode("utf-8")).digest()
        with self._lock:
            self._purge_locked(time.monotonic())
            session = self._sessions.get(session_id)
            if session is None or not hmac.compare_digest(session["MigrationTokenHash"], token_hash):
                return None
            result = {key: session[key] for key in (
                "SessionId", "Address", "Port", "ServerCertificatePem", "Name",
                "PlayerCount", "MaxPlayers", "Generation", "IsPublic", "AddressFamily",
                "ThumbnailZippedJpegBase64")}
            return {**result, **self._relay_fields()}

    def migrate(self, session_id, token, body, address):
        name, count, capacity, port, address_family, certificate, is_public, thumbnail = validate_session(body)
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
                "ThumbnailZippedJpegBase64": thumbnail,
                "AddressFamily": address_family,
                "IsPublic": is_public,
                "Generation": session["Generation"] + 1,
                "Updated": now,
                "TokenHash": hashlib.sha256(new_token.encode("ascii")).digest(),
                "RelayHeartbeat": 0,
                "RelaySequence": 0,
            })
            return {"SessionId": session_id, "Token": new_token,
                    "MigrationToken": token, "Generation": session["Generation"],
                    **self._relay_fields()}, None

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
                {**{key: value for key, value in session.items()
                    if key not in ("TokenHash", "MigrationTokenHash", "Updated",
                                   "AddressFamily", "RelaySequence", "RelayHeartbeat")},
                 **self._relay_fields()}
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
    thumbnail = body.get("ThumbnailZippedJpegBase64")
    if thumbnail is not None:
        if not isinstance(thumbnail, str):
            raise ValueError("ThumbnailZippedJpegBase64 must be base64 text.")
        if not thumbnail:
            thumbnail = None
        else:
            max_encoded_size = ((MAX_THUMBNAIL_BYTES + 2) // 3) * 4
            if len(thumbnail) > max_encoded_size:
                raise ValueError("Thumbnail exceeds the supported size.")
            try:
                thumbnail_bytes = base64.b64decode(thumbnail, validate=True)
            except (binascii.Error, ValueError) as error:
                raise ValueError("ThumbnailZippedJpegBase64 contains invalid base64.") from error
            if (len(thumbnail_bytes) > MAX_THUMBNAIL_BYTES
                    or base64.b64encode(thumbnail_bytes).decode("ascii") != thumbnail):
                raise ValueError("Thumbnail exceeds the supported size or is not canonical base64.")
    return (name, count, capacity, port, address_family,
            certificate_bytes.decode("ascii"), is_public, thumbnail)


class DirectoryHandler(BaseHTTPRequestHandler):
    server_version = "GameBuilderSessionDirectory/1"
    state = None

    def do_GET(self):
        path = urlsplit(self.path).path
        if path == "/health":
            self.send_json(200, {"Status": "ok"})
        elif path == "/v1/address":
            try:
                self.send_json(200, {"Address": self.state.request_address(self)})
            except ValueError as error:
                self.send_json(400, {"Error": str(error)})
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



class ProxyProtocolV2DirectoryServer(DirectoryServer):
    """Accept PROXY v2 and ordinary HTTP from trusted local proxies."""

    @staticmethod
    def _read_exact(connection, count, deadline):
        data = bytearray()
        while len(data) < count:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise socket.timeout("PROXY protocol v2 header timed out")
            connection.settimeout(remaining)
            chunk = connection.recv(count - len(data))
            if not chunk:
                raise OSError("incomplete PROXY protocol v2 header")
            data.extend(chunk)
        return bytes(data)

    def _proxy_client_address(self, request, client_address):
        deadline = time.monotonic() + PROXY_V2_HEADER_TIMEOUT_SECONDS
        request.settimeout(PROXY_V2_HEADER_TIMEOUT_SECONDS)
        if request.recv(1, socket.MSG_PEEK) != b"\r":
            request.settimeout(None)
            return client_address

        header = self._read_exact(request, 16, deadline)
        if header[:12] != PROXY_V2_SIGNATURE or header[12] != 0x21:
            raise OSError("invalid PROXY protocol v2 header")
        if header[13] == 0x11:
            address_size = 4
            address_type = socket.AF_INET
        elif header[13] == 0x21:
            address_size = 16
            address_type = socket.AF_INET6
        else:
            raise OSError("unsupported PROXY protocol v2 address family")

        payload_size = int.from_bytes(header[14:16], "big")
        minimum_size = address_size * 2 + 4
        if payload_size < minimum_size or payload_size > MAX_PROXY_V2_PAYLOAD_BYTES:
            raise OSError("invalid PROXY protocol v2 payload length")
        payload = self._read_exact(request, payload_size, deadline)
        source_address = str(ipaddress.ip_address(payload[:address_size]))
        source_port = int.from_bytes(
            payload[address_size * 2:address_size * 2 + 2], "big")
        request.settimeout(None)
        if address_type == socket.AF_INET6:
            return source_address, source_port, 0, 0
        return source_address, source_port

    def process_request_thread(self, request, client_address):
        try:
            client_address = self._proxy_client_address(request, client_address)
        except (OSError, ValueError):
            self.shutdown_request(request)
            return
        super().process_request_thread(request, client_address)






class UdpRelay:
    def __init__(self, state, bind, control_port, port_start, port_end):
        self.state = state
        self.bind = bind
        self.control_port = control_port
        self.port_start = port_start
        self.port_end = port_end
        self._stop = threading.Event()
        self._thread = None
        self._control = None
        self._selector = selectors.DefaultSelector()
        self._routes = {}
        self._by_socket = {}
        self._hosts = {}
        self._session_routes = {}

    @staticmethod
    def _session_id(raw):
        try:
            text = raw.decode("ascii")
        except UnicodeDecodeError:
            return None
        if "\0" in text:
            text = text.split("\0", 1)[0]
            if raw != text.encode("ascii").ljust(24, b"\0"):
                return None
        return text if text and len(text) <= 24 else None

    @staticmethod
    def _frame(kind, session_id, ident, payload=b""):
        try:
            session = session_id.encode("ascii")
        except UnicodeEncodeError:
            return None
        if len(session) > 24:
            return None
        return RELAY_HEADER.pack(RELAY_MAGIC, ord(kind), session.ljust(24, b"\0"), ident) + payload

    def start(self):
        address = ipaddress.ip_address(self.bind)
        family = socket.AF_INET6 if address.version == 6 else socket.AF_INET
        self._control = socket.socket(family, socket.SOCK_DGRAM)
        self._control.bind((self.bind, self.control_port))
        self._selector.register(self._control, selectors.EVENT_READ)
        self.control_port = self._control.getsockname()[1]
        self._thread = threading.Thread(target=self._run, name="udp-relay", daemon=True)
        self._thread.start()
        return self.control_port

    def stop(self):
        self._stop.set()
        if self._thread:
            self._thread.join()
        for route in list(self._routes.values()):
            self._close_route(route)
        if self._control:
            self._control.close()
        self._selector.close()

    def _host(self, session_id):
        snapshot = self.state.relay_session_snapshot(session_id)
        if not snapshot:
            return None
        heartbeat, _, _ = snapshot
        endpoint = self._hosts.get(session_id)
        if not endpoint or time.monotonic() - heartbeat > RELAY_HOST_TTL_SECONDS:
            return None
        return endpoint

    def _route_capacity(self, session_id):
        snapshot = self.state.relay_session_snapshot(session_id)
        if not snapshot:
            return False
        _, max_players, player_count = snapshot
        return (len(self._session_routes.get(session_id, ())) <
                min(RELAY_MAX_PER_SESSION, max(0, max_players - player_count))
                and len(self._routes) < RELAY_MAX_CLIENTS)

    def _allocate(self, session_id, nonce, endpoint):
        if not self._host(session_id):
            return
        for ident in self._session_routes.get(session_id, ()):
            route = self._routes[ident]
            if route["nonce"] == nonce:
                if route["allocation_endpoint"] == endpoint:
                    response = self._frame(
                        "O", session_id, nonce,
                        struct.pack("!H", route["socket"].getsockname()[1]))
                    self._control.sendto(response, endpoint)
                return
        if not self._route_capacity(session_id):
            return
        sock = socket.socket(self._control.family, socket.SOCK_DGRAM)
        try:
            ports = [0] if self.port_start == self.port_end == 0 else range(self.port_start, self.port_end + 1)
            for port in ports:
                try:
                    sock.bind((self.bind, port))
                    break
                except OSError:
                    continue
            else:
                sock.close()
                return
        except OSError:
            sock.close()
            return
        client_id = secrets.randbits(64) or 1
        while client_id in self._routes:
            client_id = secrets.randbits(64) or 1
        route = {"id": client_id, "session": session_id, "nonce": nonce,
                 "socket": sock, "endpoint": None, "allocation_endpoint": endpoint,
                 "updated": time.monotonic()}
        self._routes[client_id] = route
        self._by_socket[sock] = route
        self._selector.register(sock, selectors.EVENT_READ)
        self._session_routes.setdefault(session_id, set()).add(client_id)
        response = self._frame("O", session_id, nonce, struct.pack("!H", sock.getsockname()[1]))
        self._control.sendto(response, endpoint)

    def _close_route(self, route):
        ident, session_id = route["id"], route["session"]
        self._routes.pop(ident, None)
        self._by_socket.pop(route["socket"], None)
        try:
            self._selector.unregister(route["socket"])
        except (KeyError, ValueError):
            pass
        clients = self._session_routes.get(session_id)
        if clients:
            clients.discard(ident)
            if not clients:
                self._session_routes.pop(session_id, None)
        route["socket"].close()

    def _handle_control(self, packet, endpoint):
        if len(packet) < RELAY_HEADER.size or len(packet) > RELAY_MAX_DATAGRAM:
            return
        header = packet[:RELAY_HEADER.size]
        magic, kind, raw_session, ident = RELAY_HEADER.unpack(header)
        session_id = self._session_id(raw_session)
        if magic != RELAY_MAGIC or session_id is None:
            return
        payload = packet[RELAY_HEADER.size:]
        if kind == ord("H"):
            if self.state.verify_relay_heartbeat(session_id, ident, header, payload):
                self._hosts[session_id] = endpoint
            return
        if kind == ord("A") and not payload:
            self._allocate(session_id, ident, endpoint)
            return
        route = self._routes.get(ident)
        if route is None or route["session"] != session_id:
            return
        if kind == ord("X") and not payload:
            self._close_route(route)
        elif kind == ord("B") and payload and self._host(session_id) == endpoint and route["endpoint"]:
            route["updated"] = time.monotonic()
            try:
                route["socket"].sendto(payload, route["endpoint"])
            except OSError:
                pass

    def _handle_client(self, route, packet, endpoint):
        if not packet or len(packet) > RELAY_MAX_PAYLOAD:
            return
        if not self._host(route["session"]):
            return
        route["endpoint"] = endpoint
        route["updated"] = time.monotonic()
        frame = self._frame("D", route["session"], route["id"], packet)
        try:
            self._control.sendto(frame, self._hosts[route["session"]])
        except OSError:
            pass

    def _cleanup(self):
        now = time.monotonic()
        for route in list(self._routes.values()):
            if now - route["updated"] > RELAY_CLIENT_IDLE_SECONDS or not self._host(route["session"]):
                self._close_route(route)
        for session_id in list(self._hosts):
            if not self._host(session_id):
                self._hosts.pop(session_id, None)

    def _run(self):
        while not self._stop.is_set():
            try:
                ready = self._selector.select(0.25)
            except (OSError, ValueError):
                break
            for key, _ in ready:
                ready_socket = key.fileobj
                try:
                    packet, endpoint = ready_socket.recvfrom(RELAY_MAX_DATAGRAM + 1)
                except OSError:
                    continue
                if ready_socket is self._control:
                    self._handle_control(packet, endpoint)
                else:
                    route = self._by_socket.get(ready_socket)
                    if route:
                        self._handle_client(route, packet, endpoint)
            self._cleanup()


def main():
    parser = argparse.ArgumentParser(description="Game Builder direct-host session directory")
    parser.add_argument("--bind", default=os.environ.get("GB_DIRECTORY_BIND", "127.0.0.1"))
    parser.add_argument("--port", type=int, default=int(os.environ.get("GB_DIRECTORY_PORT", "47800")))
    parser.add_argument("--trusted-proxy", default=os.environ.get("GB_DIRECTORY_TRUSTED_PROXY"))
    parser.add_argument("--relay-bind", default=os.environ.get("GB_RELAY_BIND"))
    parser.add_argument("--relay-control-port", type=int,
                        default=int(os.environ["GB_RELAY_CONTROL_PORT"])
                        if os.environ.get("GB_RELAY_CONTROL_PORT") else None)
    parser.add_argument("--relay-public-address", default=os.environ.get("GB_RELAY_PUBLIC_ADDRESS"))
    parser.add_argument("--relay-data-port-start", type=int,
                        default=int(os.environ.get("GB_RELAY_DATA_PORT_START", "48000")))
    parser.add_argument("--relay-data-port-end", type=int,
                        default=int(os.environ.get("GB_RELAY_DATA_PORT_END", "48100")))
    parser.add_argument("--relay-disabled", action="store_true")
    parser.add_argument(
        "--accept-proxy-protocol-v2",
        action="store_true",
        help="accept PROXY v2 from a trusted local TLS-terminating proxy",
    )
    args = parser.parse_args()
    if not 1 <= args.port <= 65535:
        parser.error("--port must be between 1 and 65535")
    relay_enabled = args.relay_control_port is not None and not args.relay_disabled
    if relay_enabled:
        if not 0 <= args.relay_control_port <= 65535:
            parser.error("--relay-control-port must be between 0 and 65535")
        if (not 0 <= args.relay_data_port_start <= args.relay_data_port_end <= 65535
                or (args.relay_data_port_start == 0) != (args.relay_data_port_end == 0)):
            parser.error("relay data port range must be 1..65535 or 0..0")
        if not args.relay_public_address:
            parser.error("--relay-public-address is required when UDP relay is enabled")
    trusted_proxy = None
    if args.trusted_proxy:
        trusted_proxy = normalize_ip(args.trusted_proxy)
        if not trusted_proxy:
            parser.error("--trusted-proxy must be an IP address")
    try:
        bind_address = ipaddress.ip_address(args.bind)
    except ValueError:
        bind_address = None
    if args.accept_proxy_protocol_v2 and bind_address != ipaddress.IPv4Address("127.0.0.1"):
        parser.error("--accept-proxy-protocol-v2 requires --bind 127.0.0.1")
    state = DirectoryState(trusted_proxy, args.relay_public_address if relay_enabled else None,
                           args.relay_control_port if relay_enabled else None)
    relay = None
    if relay_enabled:
        relay = UdpRelay(state, args.relay_bind or args.bind, args.relay_control_port,
                         args.relay_data_port_start, args.relay_data_port_end)
        state.relay_port = relay.start()
    handler = type("ConfiguredDirectoryHandler", (DirectoryHandler,), {"state": state})
    if args.accept_proxy_protocol_v2:
        server_class = ProxyProtocolV2DirectoryServer
    else:
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
        if relay:
            relay.stop()


if __name__ == "__main__":
    main()

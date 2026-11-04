import base64
import json
import threading
import unittest
import time
import urllib.error
import urllib.request
import ipaddress
import socket
import subprocess
import sys


import server

CERTIFICATE = "-----BEGIN CERTIFICATE-----\nYWJj\n-----END CERTIFICATE-----"
UPDATED_CERTIFICATE = "-----BEGIN CERTIFICATE-----\nZGVm\n-----END CERTIFICATE-----"


def make_session(address_family=4):
    return {
        "Name": "Hosted game",
        "PlayerCount": 1,
        "MaxPlayers": 16,
        "Port": 7777,
        "AddressFamily": address_family,
        "ServerCertificatePem": CERTIFICATE,
    }


class ServerCertificateValidationTests(unittest.TestCase):
    def test_requires_a_pem_server_certificate(self):
        session = make_session()
        del session["ServerCertificatePem"]
        with self.assertRaisesRegex(ValueError, "must be PEM text"):
            server.validate_session(session)

    def test_rejects_malformed_or_oversized_certificates(self):
        session = make_session()
        session["ServerCertificatePem"] = "-----BEGIN CERTIFICATE-----\nnot base64\n-----END CERTIFICATE-----"
        with self.assertRaisesRegex(ValueError, "invalid base64"):
            server.validate_session(session)

        session["ServerCertificatePem"] = (
            "-----BEGIN CERTIFICATE-----"
            + ("A" * server.MAX_CERTIFICATE_PEM_BYTES)
            + "-----END CERTIFICATE-----"
        )
        with self.assertRaisesRegex(ValueError, "supported size"):
            server.validate_session(session)


class ThumbnailValidationTests(unittest.TestCase):
    def test_accepts_bounded_base64_thumbnail(self):
        thumbnail = base64.b64encode(b"compressed thumbnail").decode("ascii")
        session = make_session()
        session["ThumbnailZippedJpegBase64"] = thumbnail
        self.assertEqual(server.validate_session(session)[-1], thumbnail)

    def test_rejects_invalid_and_oversized_thumbnails(self):
        session = make_session()
        session["ThumbnailZippedJpegBase64"] = "not base64!"
        with self.assertRaisesRegex(ValueError, "invalid base64"):
            server.validate_session(session)

        session["ThumbnailZippedJpegBase64"] = base64.b64encode(
            b"x" * (server.MAX_THUMBNAIL_BYTES + 1)).decode("ascii")
        with self.assertRaisesRegex(ValueError, "supported size"):
            server.validate_session(session)


class RequestAddressTests(unittest.TestCase):
    @staticmethod
    def handler(peer, headers=None):
        return type("Request", (), {
            "client_address": peer,
            "headers": headers or {},
        })()

    def test_preserves_ipv6_peer_address(self):
        state = server.DirectoryState()
        handler = self.handler(("2001:0db8::5", 1234, 0, 0))
        self.assertEqual(state.request_address(handler), "2001:db8::5")

    def test_normalizes_ipv4_mapped_ipv6_peer(self):
        state = server.DirectoryState()
        handler = self.handler(("::ffff:192.0.2.9", 1234, 0, 0))
        self.assertEqual(state.request_address(handler), "192.0.2.9")

    def test_trusted_proxy_can_forward_ipv6_client(self):
        state = server.DirectoryState("192.0.2.10")
        handler = self.handler(
            ("192.0.2.10", 443),
            {"X-Forwarded-For": "2001:db8::9, 192.0.2.20"},
        )
        self.assertEqual(state.request_address(handler), "2001:db8::9")

    def test_untrusted_proxy_cannot_override_peer_address(self):
        state = server.DirectoryState("192.0.2.10")
        handler = self.handler(
            ("192.0.2.20", 443),
            {"X-Forwarded-For": "2001:db8::9"},
        )
        self.assertEqual(state.request_address(handler), "192.0.2.20")

    def test_rejects_transport_family_mismatch(self):
        state = server.DirectoryState()
        with self.assertRaisesRegex(ValueError, "family must match"):
            state.register(make_session(6), "192.0.2.9")

    def test_rejects_heartbeat_transport_family_mismatch(self):
        state = server.DirectoryState()
        registration, error = state.register(make_session(6), "::1")
        self.assertIsNone(error)
        with self.assertRaisesRegex(ValueError, "family must match"):
            state.heartbeat(
                registration["SessionId"],
                registration["Token"],
                make_session(4),
                "127.0.0.1",
            )

    def test_legacy_request_defaults_to_ipv4(self):
        body = make_session()
        del body["AddressFamily"]
        result, error = server.DirectoryState().register(body, "192.0.2.9")
        self.assertIsNone(error)
        self.assertTrue(result["SessionId"])


class SessionMigrationHttpTests(unittest.TestCase):
    def test_migration_auth_visibility_timing_and_rotation(self):
        state = server.DirectoryState()
        handler = type("TestDirectoryHandler", (server.DirectoryHandler,), {"state": state})
        directory = server.DirectoryServer(("127.0.0.1", 0), handler)
        thread = threading.Thread(target=directory.serve_forever, daemon=True)
        thread.start()
        base_url = "http://127.0.0.1:{}".format(directory.server_address[1])
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

        def request(path, method="GET", body=None, token=None):
            data = None if body is None else json.dumps(body).encode("utf-8")
            message = urllib.request.Request(base_url + path, data=data, method=method)
            if data is not None:
                message.add_header("Content-Type", "application/json")
            if token is not None:
                message.add_header("Authorization", "Bearer " + token)
            try:
                with opener.open(message, timeout=3) as response:
                    return response.status, response.read()
            except urllib.error.HTTPError as error:
                return error.code, error.read()

        try:
            public_status, public_bytes = request("/v1/sessions", "POST", make_session())
            private = make_session()
            private["IsPublic"] = False
            private_thumbnail = base64.b64encode(b"private thumbnail").decode("ascii")
            private["ThumbnailZippedJpegBase64"] = private_thumbnail
            private_status, private_bytes = request("/v1/sessions", "POST", private)
            self.assertEqual((public_status, private_status), (201, 201))
            public = json.loads(public_bytes.decode("utf-8"))
            private_registration = json.loads(private_bytes.decode("utf-8"))
            self.assertEqual(public["Generation"], 1)
            self.assertTrue(public["MigrationToken"])
            private_id = private_registration["SessionId"]

            status, listing = request("/v1/sessions")
            self.assertEqual(status, 200)
            sessions = json.loads(listing.decode("utf-8"))["Sessions"]
            self.assertEqual([item["SessionId"] for item in sessions], [public["SessionId"]])
            self.assertNotIn("Token", sessions[0])
            self.assertNotIn("MigrationToken", sessions[0])

            migration_path = "/v1/sessions/{}/migration".format(private_id)
            self.assertEqual(request(migration_path, token="wrong")[0], 404)
            status, private_info = request(migration_path, token=private_registration["MigrationToken"])
            self.assertEqual(status, 200)
            private_info = json.loads(private_info.decode("utf-8"))
            self.assertFalse(private_info["IsPublic"])
            self.assertEqual(private_info["ThumbnailZippedJpegBase64"], private_thumbnail)
            self.assertEqual(private_info["AddressFamily"], 4)

            migrate_path = "/v1/sessions/{}/migrate".format(private_id)
            self.assertEqual(request(migrate_path, "POST", make_session(), "wrong")[0], 404)
            self.assertEqual(request(migrate_path, "POST", make_session(),
                                     private_registration["MigrationToken"])[0], 409)

            state._sessions[private_id]["Updated"] = time.monotonic() - 30

            mismatched_family = make_session(6)
            status, error_body = request(
                migrate_path, "POST", mismatched_family,
                private_registration["MigrationToken"])
            self.assertEqual(status, 400)
            self.assertIn(b"family must match", error_body)
            update = make_session()
            update.update({
                "Name": "Migrated private game",
                "Port": 8888,
                "ServerCertificatePem": UPDATED_CERTIFICATE,
                "IsPublic": False,
                "ThumbnailZippedJpegBase64": base64.b64encode(b"migrated thumbnail").decode("ascii"),
            })
            status, migrated_bytes = request(
                migrate_path, "POST", update, private_registration["MigrationToken"])
            self.assertEqual(status, 200)
            migrated = json.loads(migrated_bytes.decode("utf-8"))
            self.assertEqual(migrated["Generation"], 2)
            self.assertEqual(migrated["SessionId"], private_id)
            self.assertNotEqual(migrated["Token"], private_registration["Token"])
            self.assertEqual(migrated["MigrationToken"], private_registration["MigrationToken"])
            self.assertEqual(request("/v1/sessions/" + private_id, "PUT", update,
                                     private_registration["Token"])[0], 404)
            self.assertEqual(request(migrate_path, "POST", update, migrated["MigrationToken"])[0], 409)
            status, info_bytes = request(migration_path, token=migrated["MigrationToken"])
            self.assertEqual(status, 200)
            info = json.loads(info_bytes.decode("utf-8"))
            self.assertEqual(info["Port"], 8888)
            self.assertEqual(info["ServerCertificatePem"], UPDATED_CERTIFICATE)
            self.assertEqual(info["Name"], "Migrated private game")
            self.assertEqual(
                info["ThumbnailZippedJpegBase64"],
                base64.b64encode(b"migrated thumbnail").decode("ascii"))
            self.assertEqual(info["Generation"], 2)
            self.assertFalse(info["IsPublic"])
            status, listing_after_private_migration = request("/v1/sessions")
            self.assertEqual(status, 200)
            self.assertEqual(
                json.loads(listing_after_private_migration.decode("utf-8"))["Sessions"],
                sessions)

            public_id = public["SessionId"]
            state._sessions[public_id]["Updated"] = (
                time.monotonic() - server.MIGRATION_STALE_SECONDS - 1)
            public_update = make_session()
            public_update.update({
                "Name": "Migrated public game",
                "Port": 8889,
                "ServerCertificatePem": UPDATED_CERTIFICATE,
                "IsPublic": True,
            })
            public_migrate_path = "/v1/sessions/{}/migrate".format(public_id)
            status, public_migrated_bytes = request(
                public_migrate_path, "POST", public_update, public["MigrationToken"])
            self.assertEqual(status, 200)
            public_migrated = json.loads(public_migrated_bytes.decode("utf-8"))
            self.assertEqual(public_migrated["Generation"], 2)
            self.assertNotEqual(public_migrated["Token"], public["Token"])
            status, listing_after_public_migration = request("/v1/sessions")
            self.assertEqual(status, 200)
            public_list = json.loads(listing_after_public_migration.decode("utf-8"))["Sessions"]
            self.assertEqual(len(public_list), 1)
            self.assertEqual(public_list[0]["SessionId"], public_id)
            self.assertEqual(public_list[0]["Name"], "Migrated public game")
            self.assertEqual(public_list[0]["Address"], "127.0.0.1")
            self.assertEqual(public_list[0]["Port"], 8889)
            self.assertEqual(public_list[0]["ServerCertificatePem"], UPDATED_CERTIFICATE)
            self.assertNotIn("Token", public_list[0])
            self.assertNotIn("MigrationToken", public_list[0])
        finally:
            directory.shutdown()
            directory.server_close()
            thread.join(timeout=3)

    def test_concurrent_migration_claim_has_one_winner(self):
        state = server.DirectoryState()
        registration, error = state.register(make_session(), "198.51.100.2")
        self.assertIsNone(error)
        session_id = registration["SessionId"]
        state._sessions[session_id]["Updated"] = (
            time.monotonic() - server.MIGRATION_STALE_SECONDS - 1)
        body = make_session()
        barrier = threading.Barrier(3)
        results = [None, None]

        def claim(index):
            barrier.wait()
            results[index] = state.migrate(
                session_id, registration["MigrationToken"], body, "198.51.100.3")

        threads = [
            threading.Thread(target=claim, args=(index,))
            for index in range(len(results))
        ]
        for thread in threads:
            thread.start()
        barrier.wait()
        for thread in threads:
            thread.join(timeout=3)

        self.assertFalse(any(thread.is_alive() for thread in threads))
        self.assertEqual(sum(result[1] is None for result in results), 1)
        self.assertEqual(
            sorted(result[1] for result in results if result[1] is not None),
            ["active"])
        self.assertEqual(state._sessions[session_id]["Generation"], 2)

    def test_listing_ttl_and_record_retention(self):
        state = server.DirectoryState()
        registration, error = state.register(make_session(), "127.0.0.1")
        self.assertIsNone(error)
        session_id = registration["SessionId"]
        state._sessions[session_id]["Updated"] = time.monotonic() - 61
        self.assertEqual(state.list_sessions()["Sessions"], [])
        self.assertIn(session_id, state._sessions)
        state._sessions[session_id]["Updated"] = time.monotonic() - 301
        self.assertEqual(state.list_sessions()["Sessions"], [])
        self.assertNotIn(session_id, state._sessions)


class IPv4DirectoryHttpTests(unittest.TestCase):
    def test_legacy_ipv4_request_registers_and_lists(self):
        state = server.DirectoryState()
        handler = type("TestDirectoryHandler", (server.DirectoryHandler,), {"state": state})
        directory = server.DirectoryServer(("127.0.0.1", 0), handler)
        thread = threading.Thread(target=directory.serve_forever, daemon=True)
        thread.start()
        base_url = "http://127.0.0.1:{}".format(directory.server_address[1])
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

        def request(path, method="GET", body=None):
            data = None if body is None else json.dumps(body).encode("utf-8")
            message = urllib.request.Request(base_url + path, data=data, method=method)
            if data is not None:
                message.add_header("Content-Type", "application/json")
            return opener.open(message, timeout=3)

        body = make_session()
        thumbnail = base64.b64encode(b"x" * server.MAX_THUMBNAIL_BYTES).decode("ascii")
        body["ThumbnailZippedJpegBase64"] = thumbnail

        del body["AddressFamily"]
        try:
            with request("/v1/address") as response:
                self.assertEqual(response.status, 200)
                self.assertEqual(
                    json.loads(response.read().decode("utf-8"))["Address"],
                    "127.0.0.1")
            with request("/v1/sessions", "POST", body) as response:
                self.assertEqual(response.status, 201)
            with request("/v1/sessions") as response:
                sessions = json.loads(response.read().decode("utf-8"))["Sessions"]
            self.assertEqual(len(sessions), 1)
            self.assertEqual(sessions[0]["Address"], "127.0.0.1")
            self.assertEqual(sessions[0]["ServerCertificatePem"], CERTIFICATE)
            self.assertEqual(sessions[0]["ThumbnailZippedJpegBase64"], thumbnail)
        finally:
            directory.shutdown()
            directory.server_close()
            thread.join(timeout=3)


class IPv6DirectoryHttpTests(unittest.TestCase):
    def test_ipv6_register_list_heartbeat_and_unregister(self):
        state = server.DirectoryState()
        handler = type("TestDirectoryHandler", (server.DirectoryHandler,), {"state": state})
        try:
            directory = server.IPv6DirectoryServer(("::1", 0), handler)
        except OSError as error:
            self.skipTest("IPv6 loopback is unavailable: " + str(error))

        thread = threading.Thread(target=directory.serve_forever, daemon=True)
        thread.start()
        port = directory.server_address[1]
        base_url = "http://[::1]:{}".format(port)
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

        def request(path, method="GET", body=None, token=None):
            data = None if body is None else json.dumps(body).encode("utf-8")
            message = urllib.request.Request(base_url + path, data=data, method=method)
            if data is not None:
                message.add_header("Content-Type", "application/json")
            if token is not None:
                message.add_header("Authorization", "Bearer " + token)
            return opener.open(message, timeout=3)


        try:
            with request("/v1/address") as response:
                self.assertEqual(response.status, 200)
                self.assertEqual(
                    json.loads(response.read().decode("utf-8"))["Address"],
                    "::1")
            with request("/v1/sessions", "POST", make_session(6)) as response:
                self.assertEqual(response.status, 201)
                registration = json.loads(response.read().decode("utf-8"))

            session_id = registration["SessionId"]
            token = registration["Token"]
            with request("/v1/sessions") as response:
                sessions = json.loads(response.read().decode("utf-8"))["Sessions"]
            self.assertEqual(len(sessions), 1)
            self.assertEqual(sessions[0]["Address"], "::1")

            update = make_session(6)
            update["Name"] = "Updated IPv6 game"
            update["ServerCertificatePem"] = UPDATED_CERTIFICATE
            with request("/v1/sessions/" + session_id, "PUT", update, token) as response:
                self.assertEqual(response.status, 204)
            with request("/v1/sessions") as response:
                sessions = json.loads(response.read().decode("utf-8"))["Sessions"]
            self.assertEqual(sessions[0]["Name"], "Updated IPv6 game")
            self.assertEqual(sessions[0]["ServerCertificatePem"], UPDATED_CERTIFICATE)
            with request("/v1/sessions/" + session_id, "DELETE", token=token) as response:
                self.assertEqual(response.status, 204)
            with request("/v1/sessions") as response:
                self.assertEqual(json.loads(response.read().decode("utf-8"))["Sessions"], [])

            mismatch = make_session(4)
            with self.assertRaises(urllib.error.HTTPError) as error:
                request("/v1/sessions", "POST", mismatch)
            self.assertEqual(error.exception.code, 400)
            self.assertIn(b"family must match", error.exception.read())
        finally:
            directory.shutdown()
            directory.server_close()
            thread.join(timeout=3)


class ProxyProtocolV2DirectoryHttpTests(unittest.TestCase):
    def test_proxy_v2_client_address_wins_over_spoofed_forwarded_header(self):
        state = server.DirectoryState(trusted_proxy="127.0.0.1")
        handler = type("TestDirectoryHandler", (server.DirectoryHandler,), {"state": state})
        directory = server.ProxyProtocolV2DirectoryServer(("127.0.0.1", 0), handler)
        thread = threading.Thread(target=directory.serve_forever, daemon=True)
        thread.start()
        port = directory.server_address[1]
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

        try:
            for source_text in ("198.51.100.23", "2001:db8::1234"):
                source = ipaddress.ip_address(source_text)
                destination = ipaddress.ip_address(
                    "192.0.2.10" if source.version == 4 else "2001:db8::1")
                address_block = (
                    source.packed
                    + destination.packed
                    + (45678).to_bytes(2, "big")
                    + (47800).to_bytes(2, "big")
                )
                protocol_family = 0x11 if source.version == 4 else 0x21
                proxy_header = (
                    server.PROXY_V2_SIGNATURE
                    + bytes((0x21, protocol_family))
                    + len(address_block).to_bytes(2, "big")
                    + address_block
                )
                http_request = (
                    b"GET /v1/address HTTP/1.1\r\n"
                    b"Host: localhost\r\n"
                    b"X-Forwarded-For: 203.0.113.99\r\n"
                    b"Connection: close\r\n\r\n"
                )
                with socket.create_connection(("127.0.0.1", port), timeout=3) as connection:
                    connection.sendall(proxy_header + http_request)
                    response = bytearray()
                    while True:
                        chunk = connection.recv(4096)
                        if not chunk:
                            break
                        response.extend(chunk)
                status_line, _, response_body = bytes(response).partition(b"\r\n")
                self.assertIn(b" 200 ", status_line)
                self.assertEqual(
                    json.loads(response_body.split(b"\r\n\r\n", 1)[-1].decode("utf-8"))["Address"],
                    source_text,
                )

            with opener.open("http://127.0.0.1:{}/v1/address".format(port), timeout=3) as response:
                self.assertEqual(response.status, 200)
                self.assertEqual(
                    json.loads(response.read().decode("utf-8"))["Address"],
                    "127.0.0.1",
                )
        finally:
            directory.shutdown()
            directory.server_close()
            thread.join(timeout=3)

    def test_incomplete_proxy_header_does_not_block_http_requests(self):
        state = server.DirectoryState()
        handler = type("TestDirectoryHandler", (server.DirectoryHandler,), {"state": state})
        directory = server.ProxyProtocolV2DirectoryServer(("127.0.0.1", 0), handler)
        thread = threading.Thread(target=directory.serve_forever, daemon=True)
        thread.start()
        port = directory.server_address[1]
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

        try:
            with socket.create_connection(("127.0.0.1", port), timeout=3) as slow_client:
                slow_client.sendall(b"\r")
                with opener.open("http://127.0.0.1:{}/v1/address".format(port), timeout=2) as response:
                    self.assertEqual(response.status, 200)
                    self.assertEqual(
                        json.loads(response.read().decode("utf-8"))["Address"],
                        "127.0.0.1",
                    )
        finally:
            directory.shutdown()
            directory.server_close()
            thread.join(timeout=3)


    def test_proxy_protocol_mode_rejects_non_loopback_bind(self):
        result = subprocess.run(
            [
                sys.executable,
                server.__file__,
                "--bind",
                "0.0.0.0",
                "--port",
                "47800",
                "--accept-proxy-protocol-v2",
            ],
            capture_output=True,
            text=True,
            timeout=3,
            check=False,
        )
        self.assertEqual(result.returncode, 2)
        self.assertIn(
            "--accept-proxy-protocol-v2 requires --bind 127.0.0.1",
            result.stderr,
        )


if __name__ == "__main__":
    unittest.main()

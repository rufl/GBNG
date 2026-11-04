# Game Builder session directory and UDP relay

This service is the self-hosted control plane for Unity direct-host multiplayer. It consists of two parts in one Python process:

- An HTTP session directory for public listings, host heartbeats, address discovery, and migration metadata.
- An optional UDP relay for peers that cannot reach a player-hosted game server directly.

The game remains host-authoritative. The relay forwards Unity Transport datagrams; it does not run a game server, interpret game messages, terminate the game's encrypted transport, or require a paid Unity multiplayer service. The service is dependency-free Python; the provided container uses Python 3.13.

## How a relayed session works

1. The Game Builder host starts its NGO/Unity Transport server and registers the session with the directory over HTTPS. A session ID, host bearer token, and migration token are returned. Tokens are retained by the game client; the directory stores their SHA-256 hashes.
2. When a relay is configured, the host opens an outbound UDP socket to the relay control endpoint. Every three seconds it sends an authenticated heartbeat. The relay accepts the tunnel only while the session/token are valid and the heartbeat sequence increases. It acknowledges accepted heartbeats. The host includes a relay endpoint in a copied invite only after receiving a recent acknowledgement.
3. Public listings include `RelayAddress` and `RelayPort` only while the host's relay heartbeat is less than 12 seconds old. Private sessions remain absent from public listings; they can still use relay routes through a valid invite.
4. A joining client sends an allocation request to the control port. The relay returns an available UDP data port for that client. The client points Unity Transport at the relay address and allocated port; no host-side inbound port forward is needed when the host can send outbound UDP to the relay.
5. The client sends raw Unity Transport datagrams to its allocated port. The relay wraps them in a `D` frame and sends them through the host's authenticated tunnel. The host bridge assigns a local UDP socket to that route and forwards datagrams to the local NGO server (`127.0.0.1` or `::1`, game port normally `7777`). A unique local socket per peer preserves distinct UDP endpoints for Unity Transport.
6. Replies from the host bridge return through the tunnel in `B` frames. The relay sends each payload unchanged to the client's most recently observed UDP endpoint. The host game server remains the authority for gameplay and player state.

The host sends tunnel heartbeats every 3 seconds; the directory considers a host stale after 12 seconds. An idle client route expires after 60 seconds. The Unity host bridge also removes a route after 75 seconds without client traffic if it did not receive the relay's close notification.

## UDP wire protocol

All relay control and tunnel messages use a 37-byte big-endian header, followed by an optional payload:

| Offset | Size | Field |
| --- | ---: | --- |
| 0 | 4 | ASCII magic `GBR1` |
| 4 | 1 | Message type (ASCII byte) |
| 5 | 24 | Session ID, ASCII and NUL-padded to 24 bytes |
| 29 | 8 | Message-specific unsigned 64-bit value, network byte order |
| 37 | variable | Message payload |

The Python format is `!4sB24sQ`. Current session IDs are 24 URL-safe ASCII characters. The largest accepted UDP datagram is 65,507 bytes; the maximum payload in a framed packet is 65,470 bytes.

| Type | Direction | Header value | Payload |
| --- | --- | --- | --- |
| `H` | Host → control port | Increasing heartbeat sequence | 32-byte HMAC-SHA256 of the 37-byte header. HMAC key is SHA-256 of the host bearer token's ASCII bytes. |
| `R` | Control port → host | Acknowledged heartbeat sequence | Empty. |
| `A` | Client → control port | Client-generated 64-bit allocation nonce | Empty. |
| `O` | Control port → client | The allocation nonce | Two-byte UDP data port, network byte order. Retries from the same source endpoint with the same nonce return the same route. |
| `D` | Relay → host tunnel | Relay-generated 64-bit route ID | Original client UDP datagram, unchanged. |
| `B` | Host → control port | Route ID | Original host UDP reply, unchanged. Accepted only from the current authenticated host endpoint. |
| `X` | Host or relay → control port/tunnel | Route ID | Empty; closes a route. |

Each allocation owns one UDP port from the configured data range. The relay remembers the last client endpoint that sent to that port and returns host datagrams there. It requires the data packet's source IP to match the IP used for allocation; the UDP source port may differ because Unity Transport uses its own socket.

The `H` HMAC authenticates the host tunnel. The relay does not add a separate MAC to `D`/`B` datagrams. Game traffic relies on Game Builder's Unity Transport encryption and pinned host certificate. The relay cannot inspect that encrypted game traffic.

## Configuration

The relay is disabled unless `--relay-control-port` is configured. `--relay-disabled` overrides relay settings. Relevant command-line options and environment variables:

| Option | Environment | Default | Meaning |
| --- | --- | --- | --- |
| `--bind` | `GB_DIRECTORY_BIND` | `127.0.0.1` | HTTP directory bind address. |
| `--port` | `GB_DIRECTORY_PORT` | `47800` | HTTP directory port. |
| `--trusted-proxy` | `GB_DIRECTORY_TRUSTED_PROXY` | unset | Proxy peer IP whose overwritten `X-Forwarded-For` header is trusted. |
| `--relay-bind` | `GB_RELAY_BIND` | HTTP bind address | UDP relay bind address; must be an IP literal. |
| `--relay-control-port` | `GB_RELAY_CONTROL_PORT` | unset | UDP control/tunnel port; setting it enables the relay. |
| `--relay-public-address` | `GB_RELAY_PUBLIC_ADDRESS` | unset | Hostname or IP that clients and hosts can reach; no URL scheme or port. Required when enabled. |
| `--relay-data-port-start` | `GB_RELAY_DATA_PORT_START` | `48000` | First allocated UDP data port. |
| `--relay-data-port-end` | `GB_RELAY_DATA_PORT_END` | `48100` | Last allocated UDP data port. |
| `--relay-disabled` | — | off | Explicitly disables the UDP relay. |

The `compose.yaml` profile configures UDP control port `47801` and data ports
`48000-48999`, a total of 1,000 allocatable routes. The relay enforces a
1,000-route global cap and 64 routes per source IP. Per-session routes are
limited by both a code cap of 16 and the remaining player slots; a normal
16-player session with its host already counted has at most 15 remote peers.

The standalone `0..0` UDP port range is intended only for tests that need the
operating system to choose a port. It is not compatible with the fixed Docker
port mapping or a public deployment.

The directory also enforces: up to 1,000 live/in-memory session records; public
list visibility for 60 seconds after a session heartbeat; record retention for
five minutes; migration eligibility after 30 seconds without a host HTTP
heartbeat; eight registrations per source IP per five-minute window; a 32 KiB
JSON request-body limit; 96 UTF-8 bytes per session name; and 16 KiB maximum
thumbnail payload. The selected transport address family must match the source
IP family observed by the directory. A dual-stack directory hostname may need
an A-only or AAAA-only endpoint for reliable host registration, depending on
whether the game host selected IPv4 or IPv6.

## Public Docker/Caddy deployment

Use a public server with a stable IPv4 address and a DNS A record. The default Compose relay binds IPv4 (`0.0.0.0`); an AAAA-only relay hostname or an IPv6-only player cannot use this relay. IPv6 game hosts can use it when the host can send UDP to the relay's IPv4 endpoint. Direct IPv6 hosting remains a separate path.

Because Game Builder selects the directory request route independently from
the UDP relay route, IPv6 game hosts must still reach the HTTP directory from
an IPv6 source address even when their relay tunnel uses the default IPv4
endpoint.

1. In `tools/public-session-directory`, copy `.env.example` to `.env`.
2. Set `DIRECTORY_DOMAIN` to the HTTPS directory hostname and `RELAY_PUBLIC_ADDRESS` to the DNS name or IP clients can reach over UDP. The relay address can use the same hostname as the directory if that name resolves to the relay server's public IPv4 address.
3. Keep `DIRECTORY_SUBNET`, `DIRECTORY_PROXY_IP`, and `DIRECTORY_SERVICE_IP` non-overlapping with existing Docker networks. Keep the Caddy proxy IP aligned with the fixed address in the Compose network.
4. Configure the server firewall and upstream router to allow/forward inbound TCP `80` and `443`, UDP `47801`, and UDP `48000-48999` to the Docker host. Caddy obtains HTTPS certificates and proxies the directory's HTTP API on TCP; it does not proxy UDP.
5. Start/update the stack with `docker compose up -d --build`. Set `GAMEBUILDER_SESSION_DIRECTORY=https://<DIRECTORY_DOMAIN>` for every host and client, or pass `--session-directory=https://<DIRECTORY_DOMAIN>` to the game.
6. Check `docker compose ps`, `docker compose logs -f directory`, and `https://<DIRECTORY_DOMAIN>/health`. The health endpoint checks the HTTP process only; it does not probe UDP firewall or forwarding rules.

Hosts and players need outbound UDP access to the relay. Unlike direct hosting, the host does not need an inbound forwarding rule for the game port while using the relay. Direct hosting remains available as a fallback when the relay is absent; it may still need UPnP/manual forwarding and can fail behind CGNAT.

Changing the relay control/data ports requires changing the server command and matching Compose `ports` mappings and firewall rules. Keep the configured data range at no more than 1,000 ports unless the code's global route cap is intentionally changed too. Do not expose the HTTP directory port `47800` publicly when using trusted proxy headers.

### Tailnet-only setup is not a public UDP relay

The `systemd/` units and `Caddyfile.tailnet` example serve the HTTP directory through Caddy/Tailscale. They do not pass `--relay-control-port`, so they do not run the UDP relay. The Caddy/Tailscale HTTPS listener (TCP `47801` in that example) is unrelated to UDP relay control port `47801`. Tailscale Serve and Funnel proxy HTTP(S); they do not make the public UDP relay ports available. A public relay requires reachable inbound UDP ports on the server (or a separate network-level UDP forwarding service).

## Standalone local test

From the repository root, start an isolated IPv4 loopback directory plus relay:

```text
python tools/public-session-directory/server.py \
  --bind 127.0.0.1 --port 47800 \
  --relay-bind 127.0.0.1 --relay-control-port 47801 \
  --relay-public-address 127.0.0.1 \
  --relay-data-port-start 48000 --relay-data-port-end 48099
```

Set `GAMEBUILDER_SESSION_DIRECTORY=http://127.0.0.1:47800` for a local smoke only. Plain HTTP is accepted by the game only for loopback; public directory URLs must use HTTPS. The local host/client must both be able to send UDP to `127.0.0.1` and the configured ports. Stop the server with Ctrl+C.

Run the deterministic IPv4/IPv6 directory and UDP relay tests from the repository root:

```text
python -m unittest discover -s tools/public-session-directory -p "test_*.py"
```

The relay tests cover HMAC acceptance/rejection, heartbeat acknowledgement and public-list readiness, idempotent allocations, distinct client ports, capacity/expiry, and byte-exact client-to-host-to-client datagram forwarding. Unity EditMode tests for invite parsing and certificate-pin preservation are in `Assets/Editor/DirectHostRelayInviteTests.cs` and can be run through Unity Test Runner with the `DirectHostRelayInviteTests` filter.

## HTTP directory API summary

- `GET /health`: HTTP process liveness only.
- `GET /v1/address`: request-observed IP address, accounting for the configured trusted proxy.
- `GET /v1/sessions`: active public sessions; host/migration tokens are never listed. Relay fields appear only for a live authenticated tunnel.
- `POST /v1/sessions`: create a session; returns session ID, host token, migration token, generation, and configured relay endpoint when enabled.
- `PUT /v1/sessions/{id}`: authenticated session heartbeat/update using `Authorization: Bearer <host-token>`.
- `DELETE /v1/sessions/{id}`: authenticated unregister.
- `GET /v1/sessions/{id}/migration`: fetch migration metadata using the migration bearer token.
- `POST /v1/sessions/{id}/migrate`: claim a stale session and rotate its host bearer token. The migration token remains stable across the migration.

Directory/session state, relay routes, and migration tickets are in memory only. A process/container restart loses them; hosts must register again and clients may need a fresh invite/rejoin. The service is a single-process deployment: do not put multiple independent instances behind a load balancer, because they do not share session or UDP route state.

## Security and operational limits

- Keep the directory API behind HTTPS. When trusting `X-Forwarded-For`, bind the app privately, configure the correct proxy peer IP, and ensure that proxy overwrites the header. The Compose stack uses a private Docker network and a fixed Caddy peer address.
- Host tunnel heartbeats are authenticated with HMAC-SHA256 derived from the bearer token; tokens are not sent in UDP frames. Public listing exposes session IDs and relay endpoint, not host or migration bearer tokens.
- Allocation requests are not individually authenticated. A public session ID is discoverable from public listings; an attacker may consume available relay routes for that public game or use relay bandwidth. Source-IP, session-capacity, global-route, data-port, and 60-second idle limits bound but do not eliminate denial-of-service risk. Use host/provider firewall controls and monitor bandwidth.
- Relay HMAC authenticates host heartbeats, not every forwarded datagram. Do not treat the relay as a game authorization layer. The host game and its pinned encrypted transport remain responsible for peer/session handling.
- The Compose relay is IPv4-only and single-process. There is no UDP proxying through Caddy, Tailscale Funnel, or the HTTPS directory endpoint, and no built-in persistence, cluster coordination, metrics dashboard, or SLA.
- The software avoids paid Unity multiplayer plugins/services. Public server hosting, IPv4, and traffic may still have provider costs.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| Public listing has no relay fields / copied invite is direct `GBDIRECT1` | Confirm the session directory is configured, the host is registered, `RELAY_PUBLIC_ADDRESS` is correct, host outbound UDP reaches control port `47801`, and the relay heartbeat is acknowledged. A healthy relay invite is `GBDIRECT2`. |
| Invite is `GBDIRECT2`, but allocation times out | Confirm control UDP `47801` and the complete data range are forwarded to the Docker host, the hostname resolves to the relay's reachable IPv4, and the host tunnel is still active. |
| Relay works on LAN but not from the Internet | Verify upstream router/NAT and host firewall rules for UDP, not just TCP `80/443`; Caddy's HTTPS certificate says nothing about UDP reachability. |
| Directory registration/listing fails | Check `GAMEBUILDER_SESSION_DIRECTORY`, HTTPS/TLS, Caddy/domain configuration, and `docker compose logs -f directory`. Loopback HTTP is only for local tests. |
| IPv6-only client cannot use the relay | The default relay listener is IPv4. Use direct/routable IPv6 hosting or provide an IPv6-capable UDP relay deployment; the current default Compose stack does not provide that. |
| `/health` is green but joins fail | `/health` only checks HTTP. Probe the UDP listener/range from an external network and check the host's Game Builder log for relay heartbeat or allocation errors. |

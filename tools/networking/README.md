# No-Docker matchmaking deployment

These scripts publish the existing `services/GBNG.Matchmaking` ASP.NET Core service as a self-contained .NET 10 application. They do not install a service, configure the host, open firewall ports, or start a public endpoint. Run them on the target OS (or an equivalent build environment), then deploy the output deliberately.

## Publish

Prerequisite: the .NET 10 SDK on `PATH`. From the repository root:

```powershell
.\tools\networking\publish-windows.ps1
.\tools\networking\publish-windows.ps1 -PublishDir C:\deploy\matchmaking
```

```sh
./tools/networking/publish-linux.sh
./tools/networking/publish-linux.sh /srv/matchmaking
```

Default output directories are `tools/networking/publish/win-x64` and `tools/networking/publish/linux-x64`. Relative custom paths are resolved from the repository root; absolute paths are used as given. The published executable includes the .NET runtime, but still requires the target OS, architecture, and native system libraries expected by .NET. Publish for the actual host architecture; the scripts intentionally target `win-x64` and `linux-x64` only.

## Client WebRTC extension

Internet hosting needs the official `godotengine/webrtc-native` GDExtension on every game client. LAN hosting does not. From the repository root, run:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\networking\setup-webrtc.ps1
```

The installer downloads upstream `1.2.2-stable`, verifies SHA256 `98e9446921740d995bd9ca1be48798dc3c2ceed51e044a25ce18b3cff11f56e5`, and installs it under the ignored `addons/webrtc-native/` directory. Restart Godot after installation. The extension is MIT-licensed; it bundles other permissively licensed dependencies, whose upstream notices must be preserved when redistributing the native binaries. Review the upstream release and `thirdparty/README.md` before packaging a build.

In the app, **Host game** uses LAN when no matchmaking WSS URL is configured. Internet invite-code hosting and joining require the official WebRTC extension plus a reachable `/v1/session` service address, entered under **Advanced options**. The project does not ship a public service endpoint.

## DNS, TLS, and reverse proxy

1. Choose a stable public hostname (for example `matchmaking.example.com`) and create DNS A/AAAA records pointing to the proxy host. Publish an AAAA record only if the host is reachable over IPv6.
2. Install and operate a TLS-terminating reverse proxy on that host. `Caddyfile.example` is a Caddy example: replace the hostname and load it using your normal Caddy configuration process. Automatic public certificates require correct DNS and inbound TCP ports 80 and 443; keep the proxy's certificate state backed up and protected.
3. The example proxies only `/healthz` and `/v1/session` to loopback. The service must remain bound to loopback (`http://127.0.0.1:8765`), so untrusted clients cannot bypass TLS. The app trusts forwarded headers only from loopback proxies. Keep the proxy on the same host or change application trust configuration separately (this deployment tooling does not change application code).
4. Permit inbound TCP 80/443 to the proxy as needed for HTTP-01 certificate issuance and HTTPS. Do not expose port 8765 publicly. Confirm the proxy supports HTTP/1.1 WebSocket upgrades; Caddy's `reverse_proxy` handles WebSockets. Clients must use `wss://matchmaking.example.com/v1/session`, not direct insecure `ws://` from non-local clients.

## Run the matchmaking service

Set the environment for the process and run the published executable. Keep the application on loopback; do not run it as an administrator/root account. Example Linux foreground launch:

```sh
cd /srv/matchmaking
export ASPNETCORE_URLS=http://127.0.0.1:8765
export GBNG_STUN_URLS=stun:stun.example.net:3478
export GBNG_TURN_URLS=turn:turn.example.com:3478?transport=udp
read -rsp 'TURN shared secret: ' GBNG_TURN_SHARED_SECRET; printf '\n'; export GBNG_TURN_SHARED_SECRET
./GBNG.Matchmaking
```

For STUN-only/no-TURN operation, omit the TURN URL and secret or set both to empty. Multiple STUN and TURN URLs are comma-separated. Only absolute `stun:`, `stuns:`, `turn:`, or `turns:` URLs are accepted; invalid entries are ignored by the application. When any TURN URL is configured, `GBNG_TURN_SHARED_SECRET` must be at least 32 UTF-8 bytes or startup fails. Do not put secrets in source control, shell history, command-line arguments, or world-readable environment files.

Windows foreground example (from the publish directory):

```powershell
$env:ASPNETCORE_URLS = 'http://127.0.0.1:8765'
$env:GBNG_STUN_URLS = 'stun:stun.example.net:3478'
$env:GBNG_TURN_URLS = 'turn:turn.example.com:3478?transport=udp'
$env:GBNG_TURN_SHARED_SECRET = Read-Host 'TURN shared secret'
.\GBNG.Matchmaking.exe
```

For continuous operation, configure your host's service manager/supervisor yourself to run the executable as an unprivileged dedicated account with the environment above, a restricted writable working directory, restart-on-failure, and protected secret storage. The publish scripts make no persistent machine changes. After starting, verify locally with `curl http://127.0.0.1:8765/healthz` (or the equivalent HTTP client); a healthy process alone does not establish that public DNS, TLS, WebSockets, or TURN connectivity work.

## Optional coturn relay

Install and manage coturn separately. Copy `coturn.conf.example` into a protected host configuration location; replace `realm` and the explicit `static-auth-secret` placeholder using a secure secret-management method with a securely generated random secret of at least 32 UTF-8 bytes. Configure that *same* secret as `GBNG_TURN_SHARED_SECRET` for the matchmaking process. Restrict access to the coturn config because it contains the shared secret. Do not commit the filled-in file. The service issues time-bounded HMAC-SHA1 REST credentials from this shared secret; clients receive temporary credentials, not the secret itself.

Set `GBNG_TURN_URLS` to the TURN URLs that clients should use, e.g. `turn:turn.example.com:3478?transport=udp`; configure corresponding public DNS and coturn listeners. Add other comma-separated TURN URLs only when the relay is configured to serve them. The example uses standard TURN port 3478 and a deliberately narrow UDP relay allocation range 49160–49200. Open inbound UDP and TCP 3478 and inbound UDP 49160–49200 on the host firewall and any upstream firewall/NAT. Ensure return traffic is allowed. A relay range that is too small can run out of allocations; size it to expected concurrent relayed sessions and monitor capacity.

When coturn is behind NAT, set its `external-ip` mapping (and appropriate private `listening-ip`/`relay-ip`) as shown in the config comments. Ensure the mapped address is actually routable and ports are forwarded unchanged. TURN over TLS is not enabled in the example; if required, separately configure coturn certificates/listeners and matching `turns:` URLs. Avoid publicly reachable coturn administration interfaces and never use unauthenticated/open relay settings. Use the identical shared secret only between coturn and the matchmaking process, rotate it in both places together, and plan for credentials issued before rotation to stop working.

## Security boundaries and limitations

- This service handles private room codes and WebRTC signaling only. Game traffic is peer-to-peer; TURN carries traffic only when WebRTC requires a relay. This is not a game-data server, room persistence system, or general-purpose proxy.
- TLS/WSS protects client-to-proxy signaling. Protect host access, proxy configuration, deployment artifacts, environment secrets, TURN configuration, and backups. The sample proxy does not add authentication, rate limiting, monitoring, or access policy beyond exposing the two service paths; add suitable controls at the edge for your threat model.
- The eight-character invite code is a bearer credential: anyone who receives it can join while the room has capacity. There are no user accounts, parental controls, or per-player identity checks. Join attempts are rate-limited per source IP; share codes only with intended players.
- The signaling service sees SDP, ICE candidates, and source-IP metadata, but does not receive game packets. WebRTC data channels use DTLS encryption; coturn relays encrypted game traffic only when direct ICE paths fail, while still observing network metadata and bandwidth.
- TURN capacity, bandwidth, abuse controls, and costs belong to the coturn operator. The service cannot ensure that firewalls/NAT permit direct ICE connectivity; TURN can increase reliability but consumes relay bandwidth. Test representative clients and restrictive networks.
- `GET /healthz` reports process health only. It does not test coturn, DNS, certificates, proxy WebSocket forwarding, or ICE connectivity. Monitor those dependencies independently, rotate and audit credentials, apply OS/.NET/coturn/proxy security updates, and review logs/available disk and relay capacity.
- This example is a deployment starting point, not a managed/high-availability deployment. It provides no clustering, persistent room state, automatic backups, host hardening, or automatic upgrades.

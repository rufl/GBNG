This is an unofficial, community-maintained fork of Google Game Builder. Active development is on the `unity` and `godot` branches.

# Game Builder

![Game Builder](gb-hero.jpg)

Welcome to the Game Builder open-source repository. This is the source
code to build Game Builder, an application that allows users to
create games with little or no coding experience. We are excited to share
the source code with the community and are looking forward to seeing
what you will create!

This is not an officially supported Google product.

## Fork branches

This fork keeps the implementations separate:

* [`unity`](https://github.com/rufl/GBNG/tree/unity) is the actively developed Unity 6 continuation, with restored editor features, a bundled Monaco code editor, and player-hosted NGO/UTP multiplayer without paid Unity networking services.
* [`godot`](https://github.com/rufl/GBNG/tree/godot) is the GBNG — New Gen rewrite, targeting Godot 4.7.2 .NET, .NET 10, and ClearScript/V8 7.5.1.1 while reusing the original `.voos` projects and assets.

## Prerequisites

* Unity Editor 6000.6.6f1. This fork is pinned to that editor release.
* Unity's C# compiler (C# 9.0 language level). Unity controls the language version; use only the features supported by its compiler.
* [Python 3.15.2](https://www.python.org/downloads/release/python-3152/) for code generation. `.python-version` records the selected version.
* Unity packages: Test Framework 1.8.0, UGUI 2.6.0, and Post Processing 3.5.4. TextMesh Pro functionality is supplied by UGUI in this editor.
* Unity 6 no longer supplies the legacy UNet serializer. `VoosBinaryWriter` and `VoosBinaryReader` now own the existing little-endian game-state format instead of depending on the unsupported HLAPI package.
* The native V8 Unity wrapper source and regression harness are in `v8_in_unity/`; V8 engine source and fetched dependencies remain upstream. The recorded engine revision is V8 7.1.0.0 (`d2e0166ded485df126c765b9196f7edd1ce50f82`). [`v8_in_unity/BUILDING.md`](v8_in_unity/BUILDING.md) documents reproducible Windows, Linux, and macOS universal rebuild/test paths.
* On Windows, Git Bash (included with [Git for Windows](https://gitforwindows.org/)) to run shell tools. On macOS, use Terminal.

## Project layout

* `Assets/` — Unity scenes, scripts, prefabs, art, audio, and runtime resources. Keep Unity asset paths and `.meta` GUIDs intact.
* `Packages/` — Unity Package Manager dependencies.
* `v8_in_unity/` — native plugin wrapper, C ABI, platform projects, and regression harness; see [`BUILDING.md`](v8_in_unity/BUILDING.md).
* `ProjectSettings/` — pinned Unity editor version and project settings.
* `tools/codegen/` — Python generators and the `gen-all.sh` entry point.
* `tools/integrations/` — legacy Photon migration patches and Monaco JavaScript-highlighting maintenance scripts; current multiplayer does not use PUN.
* `tools/public-session-directory/` — dependency-free HTTP session directory and optional UDP relay; see its [operations and protocol guide](tools/public-session-directory/README.md).
* `tools/licensing/`, `tools/documentation/`, and `tools/maintenance/` — license, documentation, and maintenance utilities.
* `tools/asset-inventory/` — generated visual asset and license catalog.
* `third_party/` and `Assets/third_party/` — vendored dependencies with their individual notices.
* `docs/` and `builds/` — generated scripting docs and historical build archives.

## Running code generation

Run `bash tools/codegen/gen-all.sh` from the repository root with Python 3.15.2. The generators update marked regions in Unity C# and JavaScript source files.

## Running in Unity Editor

1. Open the repository root with Unity Editor 6000.6.6f1.
1. Open the splash screen scene: `Assets/splash.unity`.
1. TextMesh Pro essential resources are included under `Assets/TextMesh Pro`; no manual import is required.
1. Click Play.
On normal shutdown, player control is not migrated while the controlled actors are being destroyed; the existing save/quit prompt is unchanged.
Windows standalone builds produced by `Game Builder | Build` use Unity's direct rendering mode in `boot.config`. This avoids the reproduced native shutdown crash in threaded mode on D3D11, D3D12, and the WARP software renderer. Direct mode reduces CPU-side rendering parallelism and may affect render-heavy frame times; Linux and macOS builds are unchanged. The native V8 runtime is deinitialized when the player quits.



## Building a standalone player

Use `Game Builder | Build` to package the currently selected standalone target. This menu runs the fork's platform-specific packaging steps; output goes to `build_output`. For macOS Intel, select the `StandaloneOSX` target and use `Game Builder | Build macOS Intel (Rosetta)`, which writes `build_output_macos_intel_rosetta`. The bundled macOS V8 runtime is x86_64-only, so native Apple Silicon builds are rejected.

The archives in `builds/` are historical binaries and do not represent a build of the current source. See [the archive notes](builds/README.txt).

## Feedback

The in-game Feedback action submits the typed message to the legacy Google
Forms endpoint recovered from the archived Steam player. It requires internet
access; Google handles the submission and may collect service-side metadata.
The client uses UnityWebRequest and does not require a paid Unity plugin or API
key.

## Scripting documentation

For documentation about how the in-game scripting works, open the `docs.html`
file in a browser (after downloading/cloning this repository).

## Asset and license inventory

Regenerate the catalog with `python3 tools/asset-inventory/generate.py`. Serve the repository root with `python3 -m http.server 8000`, then open `http://localhost:8000/tools/asset-inventory/`.

The catalog lists license evidence and marks files without a located notice as unidentified; third-party terms remain separate from the project license.

# Optional integrations

The core Unity editor runs without paid Unity Asset Store plugins. Built-in 2D drawing, local OBJ/FBX model loading, actor libraries, project saving, and direct-host multiplayer are part of the baseline. Steam Workshop uses the included Steamworks.NET package but requires the Steam client, a logged-in account, and a valid App ID. Optional online services may require an account, API key, or internet access.
Workshop browsing refreshes when the menu is reopened; changing search or sort
returns to the first page. The Game Library ignores subscribed-item results from
previous opens or refreshes; actor and card Workshop menus also reject stale
results after close, search, sort, or page changes.
Workshop image previews reuse one cached Sprite per URL across displays, avoiding a new Unity Sprite allocation on every request.
The shared Game Library detail panel releases its generated thumbnail Sprite when replaced or destroyed, without destroying the caller-owned texture.
Featured and Workshop detail panels share that ownership rule, releasing their
generated thumbnail Sprites on replacement or teardown while preserving the
downloaded textures.
SaveMenu releases screenshot Sprite wrappers and its own captured textures on
replacement or teardown, while preserving caller-owned textures. Snapshot
popups release both their generated Sprite and captured Texture2D when they
close. The Steam actor-upload dialog releases its captured thumbnail on
replacement or close. Library category and custom terrain preview Sprites are
released on refresh or teardown; the icon picker disposes its cached Sprites
and owned textures when its loader is destroyed.
ColorGradientFieldUI reuses its generated preview texture at a stable width and
releases it on resize or teardown without clearing caller-assigned textures.
Poly Toolkit's asset browser releases its generated loading texture and cached
thumbnails when its editor window closes; late thumbnail fetches are destroyed.
Imported `AssetDatabase` textures remain project-owned.
The browser reuses its editor GUIStyles across repaints and drops their references
when the window closes.
SnapshotCamera restores the prior active render target for both capture paths.
Game-view capture also restores its camera target and enabled state, plus motion
blur if rendering fails.
Actor captures restore the helper camera's prior world pose before later captures.
ImageLoader releases per-URL Sprite and Texture caches and clears in-flight
requests when the service is destroyed; late Workshop callbacks are ignored.

Public-session thumbnails own their decoded textures and release them when
their thumbnail items are destroyed or refreshed. Compressed thumbnail decode
failures also dispose their temporary texture.

Scene actor and built-in prefab libraries cache decoded thumbnails and own those
textures until teardown. Scene actor thumbnails replaced or deleted during the
session remain alive until teardown because existing search results may still
reference them. Script-driven built-in prefab spawns skip thumbnail decoding.
Actor prefab placement deep-copies its saved template before remapping actor and
brain IDs, so repeated placements preserve the original hierarchy, behaviors,
and actor references.


Terrain import progress items own and release their generated thumbnail Sprite
and decoded image. Game bundle thumbnails are shared by `GameBundleLibrary`;
replaced and deleted images remain valid for existing UI references and are
released when the library is destroyed.

Template thumbnails load once even while their menu item is inactive. Requests
are disposed on completion, replacement, or teardown; generated image objects
are released when replaced or torn down.

Feedback submissions continue after the form closes; network failures are logged.

Failed HTTP sound downloads are reported and are not cached as audio clips.

Web image searches abort their active API and thumbnail requests when cleared,
replaced, or torn down; late callbacks are ignored and unclaimed textures released.
Each library owns its cancellation handle, so canceling one search leaves others running.
Null Wikimedia page entries are skipped while valid pages remain ordered by search index.

The synthetic Custom behavior-card panel persists its display order per actor in
brain metadata, separate from the behavior-use order that controls runtime execution.

The built-in UGUI replacement resolves the archived procedural-image component
references, including rounded-corner modifiers, borders, stencil masks, and UI
clipping. It does not require the paid Asset Store package.

The free 2D renderer now uses the original ProggyCleanTT BMFont metrics and
bitmap atlas from the archived release; those font assets are distributed under
the font's MIT license. The in-tree Unity GL renderer reads the BMFont XML
directly, with no Shaper plugin dependency.

The ProggyClean BMFont stores glyph cells in an atlas orientation that differs
from Unity's GL texture coordinates; the renderer maps each cell with the
correct vertical orientation. The main camera keeps PostProcessing's final
blit enabled, while its obsolete `GameUiRenderer` image-effect pass-through is
disabled so it cannot overwrite the processed scene.

## Common instructions

- Running shell scripts: Whenever these instructions mention that you must run a
  given shell script, it's implied that you must run it (e.g 'bash shellscript.sh') in Git Bash (or OSX
  terminal) at the root of the repository.

- Define a C# directive: When these instructions tell you to "define a C#
  directive" this means adding a C# #define symbol. To do this:
  * In Unity, open `File | Build Settings`
  * Go to `Player Settings | Other Settings | Configuration`
  * Add the directive to the end of the `Scripting Define Symbols` text field (with a semi-colon before it)
  * Restart Unity


## Importing local 3D models

1. Select an actor in Build mode.
1. Open the in-game command console and run:

   ```text
   renderuri localfbx:C:/path/to/model.fbx
   ```

   Use an absolute path. Paths containing spaces may be quoted. OBJ files use
   the in-tree Wavefront loader; MTL diffuse color/alpha and `map_Kd` textures
   load from the OBJ directory or its subdirectories.
1. FBX and other Assimp-supported model files use the pinned
   `com.donghok.meshimporter` package (UnityMeshImporter and AssimpNet are MIT;
   Assimp is BSD). It imports static/skinned mesh hierarchies, diffuse
   materials, external diffuse textures, and legacy transform animation clips
   without TriLib; missing or undecodable diffuse textures fall back to
   material color. Failed imports release partially created Unity meshes,
   materials, and decoded diffuse textures. Animation clips loop from the first
   clip; mesh and sidecar files remain external and are not embedded in a saved
   project.

Imported OBJ/FBX mesh, collider, material, and texture objects are shared with
clones and released when the cache entry and its last clone are disposed.
Transient create/spawn previews and avatar tinting keep their own material
instances and release them with the owning component. UserBody immediately
destroys a per-renderer tint copy once AvatarMaterialControl or another owner
replaces it, then destroys its active tint copies at teardown.
Preview renderers share their assigned instance instead of creating implicit
per-renderer material copies.

AssetCache owns each downloaded image's material and texture with its cache
entry; runtime clones retain those resources until the final lease is released.

GrassSpawn unregisters its material set from GrassBend and destroys its owned
material instances when the component is torn down.

Tool-effect tint materials are explicitly owned per renderer and released with
their effect component.

JointControl and JointFeedback tint their line/child renderers with component-owned
material instances and destroy those instances on teardown, leaving shared source
materials unchanged.

SelectionFeedback and the move/rotate gizmos clone tint materials per tool
instance, preserve selection and axis highlighting, and release those instances
when the tool is destroyed.

Terrain placement tint uses per-renderer property blocks for remote, delete, and
edit ghosts. The create tool shares one component-owned material across local
ghosts. Create, delete, and edit preview components release their tracked ghosts
on teardown without modifying shared source materials.
`TerrainRendering` releases generated per-color prefab/material variants and
clears cell/edge render state when its component is destroyed.
`TerrainManager` owns downloaded custom-style textures, releases replaced and
retired textures after thumbnail listeners refresh, and ignores stale workshop
downloads for styles that are no longer active.
`TerrainSystem` owns its generated chunk render/collider meshes, texture arrays,
and cloned terrain material. Reconfiguring dimensions or rebuilding arrays
releases replaced resources; teardown clears shader globals only when they
still point at that terrain instance's arrays. `ProceduralCube` owns and
releases its generated mesh on component teardown.
The `test terrain styles` diagnostic builds retained per-style pixel buffers
directly, without allocating temporary `Texture2D` objects.

Actor renderable tint and `_Running` shader state use per-renderer property
blocks, preserving actor-specific appearance while sharing source materials.
Actor bounciness uses actor-owned `PhysicsMaterial` copies per collider and
releases them when the renderable is replaced or the actor is destroyed.
`GameUiMain` releases its `Draw2DUnityImpl` UI material on teardown; invalid font
configuration fails before that runtime material is allocated.

GameBuilderStage destroys its per-scene skybox and ground material instances on
teardown. It restores the previous global skybox only when RenderSettings still
references an instance owned by that stage; caller-owned temporary skyboxes
remain untouched.

The full MIT/BSD notices ship with the application at
`StreamingAssets/third_party/UnityMeshImporter-LICENSE.md`.

The legacy URI scheme is named `localfbx:` for save compatibility.

## Direct offline save/load

The in-game console can save or load a `.voos` file by absolute path without a
file-browser plugin:

```text
save "C:/path/to/project.voos"
load "C:/path/to/project.voos"
```

The normal Save menu continues to store projects in Game Builder's local
library.
Queued saves signal completion only after their file-write job has completed, so save-before-exit and autosave callbacks cannot run while state capture is still pending.
Empty or incomplete `.voos` files are rejected before a scene reload, preserving the current project and reporting the malformed save rather than failing later during world load.

Ground materials using shaders without recognized tint properties keep their
authored color instead of aborting scene load.

## Behavior code editor

Unity packages the Monaco assets and free UnityWebBrowser CEF engine in Windows
x64, Linux x64, macOS x64 and macOS ARM64 standalone builds. Cross-builds for
Linux x64 and both macOS architectures succeed; this verifies packaging, not
all native platform combinations. The Monaco/CEF player path has been runtime-
smoked on Windows x64 and on Linux x64 with Unity 6000.6.6f1 Mono on Ubuntu
26.04 under WSL2/WSLg and Mesa llvmpipe. Linux smoke confirmed the CEF page
ready signal, a 1280x720 browser texture, and JS editor-code round-trip. Physical
Linux hardware and GPU-specific rendering are not yet validated. CEF does not
require WebView2, Direct3D 11/12 or a paid Unity plugin.

It provides syntax highlighting, completions, behavior-system typings and
source navigation, editing, read-only mode, diagnostics, scroll restoration
and zoom. If CEF cannot start or Monaco assets are missing, the built-in
multiline editor remains available. The browser starts lazily at the visible
code panel's pixel dimensions; Unity scales that texture after layout changes
instead of resizing the browser process.

UnityWebBrowser's Resources folder includes a browser input handler implemented
with Unity's Input System. The project installs free `com.unity.inputsystem`
1.20.1 and enables both input backends (`activeInputHandler: 2`), so that
resource's script is compiled and Unity no longer reports it as missing.
Monaco explicitly creates `WebBrowserOldInputHandler`, so its browser input
stays on the legacy backend. No UI input module or Input Actions asset was
migrated.

Keyboard/mouse gameplay remains on Unity's legacy Input Manager. Gameplay also
reads Unity's free Input System `Gamepad` layout: left stick moves, right stick
looks using the existing look-sensitivity and vertical-inversion options;
triggers move vertically while flying. South/A jumps, North/Y changes camera
view, West/X triggers the primary action, East/B the secondary action,
left-stick click sprints, and Start pauses/resumes the simulation. Jump and the
primary/secondary actions are disabled in Build mode; camera-view switching
remains available. With an active UI selection, D-pad moves between controls,
South/A submits, and East/B cancels; these face buttons do not trigger gameplay
actions while UI focus is active. Stick or trigger input clears UI selection and
returns face buttons to gameplay. If nothing is selected, the first D-pad press
selects the first active, interactable control with navigation enabled. The
existing keyboard/mouse input module and bindings remain unchanged.
The archived Rewired module serialized the `Horizontal`, `Vertical`, `Submit`,
and `Cancel` action names, but its original per-button assignments were not
recovered. This replacement reads `Gamepad.current`; it has no per-player device
pairing or rebinding. The archived data does not establish whether the Steam
release exposed either feature. The mappings are not byte-for-byte parity, and
physical-controller validation remains outstanding.
The virtual-device regression suites in `Assets/Editor/GamepadInputControlTests.cs` and `Assets/Editor/GamepadInputEdgeTests` pass 7/7 under Unity 6000.6.6f1, covering gameplay bindings, input edges, UI navigation, and Build-mode gating; they do not replace physical-controller validation.

Assigned behavior labels use `getDescription()` when it returns a non-empty
value; otherwise, the first-line `//` comment is the fallback label.
Behavior icon sprites are cached by name and shared between cards and the picker; late results for destroyed UI targets are ignored.
User behavior-library cards stored as `.js` files in
`GameBuilderUserData/Behaviors` beside the standalone player appear in the Card
Library. Selecting one imports it into the game as an embedded behavior, so
shared games do not depend on a local file. User-library URIs accept only a
single file name and cannot escape that directory. If the player directory is
unwritable, Game Builder stores `GameBuilderUserData` under Unity's persistent
data directory instead; this fallback is not covered by Steam Cloud.

The bundled CEF runtime increases desktop build size and requires the Mono
scripting backend; UnityWebBrowser does not support IL2CPP. Linux x64 now has a
pinned `libv8_in_unity.so` artifact. A Unity 6000.6.6f1 Mono player on Ubuntu
26.04 under WSL2 loaded it, evaluated `6 * 7 == 42`, and exercised
`DirectHostDiscovery` without the former `GetIPv6Properties` exception; Linux
falls back to libc's `if_nametoindex` when Mono lacks that API.
Linux x64 builds rewrite AssimpNet's four `libdl.so` imports to `libdl.so.2`
and bundle `libminizip.so` beside Assimp, preloading it globally to satisfy
Assimp's `libminizip.so.1` dependency without `LD_LIBRARY_PATH`.
A Linux Mono player smoke on Ubuntu 26.04 under WSL2/WSLg imported the repository's animated Fox FBX through Assimp and ran asset preparation. It produced a skinned mesh with 1,733 vertices and 19 bones, three animation clips, two diffuse textures, and a generated mesh collider; sampling `RunForward` changed the rig pose.

A fresh `StandaloneLinux64` player built with `GameBuilderMenu.Build` also started under WSLg and stayed alive for a 35-second smoke run using Mesa llvmpipe. Steam Workshop was unavailable because no Steam client was running; this did not prevent player startup.

When an FBX texture's original path is unavailable, the importer checks the model directory and then maps `GameAssets/Models/...` to the parallel `GameAssets/Textures/...` tree. The EditMode smoke confirms that both Fox textures load from that tree.

The EditMode regression suite in `Assets/Editor/AssetCacheIntegrationTests` covers `AssetCache.Get` for a local OBJ and the Assimp-backed Fox FBX. It checks deferred callbacks, concurrent-request coalescing, deferred cache hits, skinned-mesh import, and collision geometry. Physical Linux GPU rendering and cross-host multiplayer remain untested: the available tailnet peer answered reachability probes, but remote access did not permit deploying or launching a Game Builder player.

The checked-in macOS V8 bundle is x86_64-only. The macOS build guard reads its
Mach-O slices and therefore permits ARM64 or universal player builds only after
a matching V8 bundle has been rebuilt and validated. On Apple Silicon, use
`tools/maintenance/build_v8_in_unity_macos.py --architecture universal` with
the pinned V8 source, Xcode, depot_tools, and Rosetta 2; it builds and runs both
native harnesses before installing the merged bundle. Until such an artifact
passes its standalone Unity behavior-code smoke, select `StandaloneOSX` and
use `Game Builder > Build macOS Intel (Rosetta)`. macOS CEF apps are unsigned
and must be signed/notarized for public distribution.

UnityWebBrowser and the CEF engine packages are pinned at 2.2.8. Windows
players contain `LICENSE.UWB.txt`, `LICENSE.CEF.txt` and `README.CEF.txt`
beside the CEF binaries in `GameBuilder_Data/UWB`; macOS apps include the
UWB license and CEF third-party notices in `Contents/Resources`. CEF's README
points to `about:credits` for Chromium and other bundled third-party notices.
UniTask 2.5.10 is embedded under `Packages/com.cysharp.unitask`, with its
upstream MIT license at `LICENSE`. Only the optional Editor task-tracker code
is omitted because it no longer compiles against the pinned Unity release.

## Optional integrations available for free

### Multiplayer: player-hosted direct connections

The Unity path uses Unity Netcode for GameObjects and Unity Transport; neither
Photon PUN nor a paid Unity service is required.
- A joining player receives the active behavior database, scene card-pack and actor-pack metadata, stage, terrain, and Sojo state before gameplay begins.
- In builds without Steamworks, a missing local Workshop asset is requested from the direct host on demand. Transfers are chunked, capped at 64 MiB per asset, and path-validated before storage; an unanswered request fails after 30 seconds or immediately when the direct-host connection closes. The two-player player smoke creates an asset with a nested file on the host and verifies both files after the client fetches it.

- From the splash screen, open **Multiplayer** and choose **Host a game** for
  the unchanged IPv4 host mode. Choose **Host IPv6 game** for an IPv6-only host.
- Search hosts and public sessions by name; sort by name or player count and use
  **Refresh** to request updated listings.
- Other players on the same IPv4 LAN see public IPv4 hosts in the room list.
  Private hosts are omitted from IPv4 and IPv6 LAN discovery but remain joinable
  by manually sharing their address. Switching a discovered host to private
  sends an IPv4 broadcast or IPv6 multicast withdrawal; missed or unrecognized
  withdrawals leave cached entries visible for up to six seconds. Use
  **Join by invite** and paste the host's `GBDIRECT1` direct invite or, when
  its self-hosted relay is active, the `GBDIRECT2` relay invite. Both formats
  pin the host certificate; hostnames in a valid invite resolve IPv4-first
  with IPv6 fallback. Use **Copy invite** in the multiplayer panel to share it.
- The IPv4 host listens on UDP port `7777`; LAN discovery broadcasts on UDP
  `47778`. Allow UDP `47778` through the local firewall for IPv4 broadcasts and
  IPv6 multicast discovery.
  Internet hosting tries to create an automatic UPnP IGD mapping to local UDP
  `7777`; if external port `7777` is occupied, it tries `17777` and `27777`.
  The public room list advertises the selected external port; IPv4 LAN discovery
  remains on `7777`.
- UPnP mapping is best-effort and requires an enabled, compatible router. Allow
  inbound UDP `7777` through the host's operating-system firewall; UPnP does not
  configure that firewall. If automatic mapping is unavailable, manually forward
  UDP `7777` to the host. CGNAT or restrictive NAT can still block Internet joins.

**Host IPv6 game** binds an IPv6-only socket on UDP `7777`; it does not use
UPnP or IPv4 LAN broadcast discovery. Peers on the same IPv6 link discover it
through IPv6 multicast on UDP `47778`. It can also appear in the public room list
when the directory is configured and the host reaches that endpoint over IPv6.
The directory checks that the request source family matches the host transport,
so a directory URL resolving to both address families may need an A-only or
AAAA-only hostname for predictable registration. Share the `[IPv6]:7777`
address manually if no public directory is available. Allow inbound UDP `7777`
through the host firewall and, where applicable, the router's IPv6 firewall. A
globally routable IPv6 address is needed outside the local network; a ULA works
only where its prefix is routed. If no usable IPv6 address is found, the panel
falls back to `::1`, which is only reachable from the host itself.

- A self-hosted session directory lists only **Public Multiplayer** sessions.
  With the directory configured, both public and private direct-host sessions
  register their endpoint and certificate so existing peers can migrate after
  a host loss. Private records stay out of public listings but their endpoint,
  name, and certificate remain in the directory's memory for up to five minutes.
  Without the directory, LAN discovery and local-network manual invites still
  work, but automatic host migration and public-address invite resolution are
  unavailable. The directory returns the host's request-observed IP through
  `/v1/address`. When the UDP relay is enabled, it advertises a relay endpoint
  only while the host's authenticated tunnel is healthy. The relay forwards
  encrypted Unity Transport datagrams; it does not terminate transport
  encryption or make the host authoritative.
  Public directory listings carry each game's zipped JPEG thumbnail when it
  fits the 16 KiB limit; missing or oversized thumbnails use the generic image.

Direct-host sessions may require an optional shared password. Hosts choose it
when creating the game; players joining from LAN discovery, public listings, or
a direct invite are prompted to enter it. Leave it blank when the host has not
set one. Passwords must be empty or 8–128 UTF-8 bytes. The host checks the
password before admitting a remote connection; it travels inside the encrypted
Unity Transport session authenticated by the host's pinned certificate.
It is never included in an invite, public listing, or directory record; share it
separately with players. This is shared-secret access control, not account
identity: player names remain client-supplied.

#### Self-hosted public session directory

The dependency-free directory service is `tools/public-session-directory/server.py`.
For local testing, run it with Python 3.7 or newer:

```text
python tools/public-session-directory/server.py --bind 127.0.0.1 --port 47800
set GAMEBUILDER_SESSION_DIRECTORY=http://127.0.0.1:47800
GameBuilder.exe
```

Run the IPv4/IPv6 service regression tests with:

```text
python -m unittest discover -s tools/public-session-directory -p "test_*.py"
```

For a local IPv6 loopback smoke, start the service with:

```text
python tools/public-session-directory/server.py --bind ::1 --port 47800
```

Then set `GAMEBUILDER_SESSION_DIRECTORY=http://[::1]:47800`. Loopback HTTP is
accepted for tests only.
Unity blocks plain HTTP to IPv6 loopback by default. For this smoke only,
temporarily set the Player Settings Insecure HTTP Option to Always Allowed, then
restore its prior value. Public directory endpoints must use HTTPS.

Set the same `GAMEBUILDER_SESSION_DIRECTORY` value for hosts and browsers, or
pass `--session-directory=https://sessions.example.org` to the game executable.
Deploy the service behind an HTTPS reverse proxy. Set `--trusted-proxy` to the
proxy's source IP and ensure it overwrites `X-Forwarded-For`; never expose the
application port publicly when trusting proxy headers. The Compose stack below
uses fixed private addresses for Caddy and the service; adjust its subnet if it
overlaps an existing Docker network.

#### Containerized deployment with HTTPS

Point a DNS A record at the server's public IPv4 address, or an AAAA record at
its public IPv6 address. The Docker host and network must accept inbound TCP
ports `80` and `443` over the advertised family. Hosts must reach the directory
over the same address family as their game transport; use a family-specific
hostname if clients would otherwise select the other route.
Then, from a terminal:

```text
cd tools/public-session-directory
copy .env.example .env
notepad .env
docker compose up -d --build
```

Set `DIRECTORY_DOMAIN` in `.env` to that hostname. The Caddy container obtains
and renews HTTPS certificates automatically when the domain resolves to this
server and inbound TCP ports `80` and `443` reach it. Caddy publishes those TCP
ports and proxies the HTTP API; port `47800` stays on the private Docker
network. Compose publishes the UDP relay ports directly. Set
`GAMEBUILDER_SESSION_DIRECTORY=https://<DIRECTORY_DOMAIN>` for every host and
client, or pass the matching `--session-directory=` argument. Change
`DIRECTORY_SUBNET`, `DIRECTORY_PROXY_IP`, and `DIRECTORY_SERVICE_IP` in `.env`
if the default Docker subnet conflicts with another network.

The Compose stack also runs the self-hosted UDP relay. Set
`RELAY_PUBLIC_ADDRESS` to a DNS name or IP reachable by hosts and players.
Open inbound UDP `47801` (relay control) and UDP `48000-48999` (allocated game
routes) on the server and forward them to this Docker host. Caddy/Funnel
terminate HTTPS only; neither proxies UDP. Hosts and players need outbound UDP access to the relay.
The default Compose relay binds IPv4. For IPv6-only players, the optional
`compose.ipv6.yaml` overlay enables a dual-stack UDP relay; Docker and the
server host must also support IPv6 and publish the relay ports over IPv6.
Details and setup are in
[`tools/public-session-directory/README.md`](tools/public-session-directory/README.md).


##### Tailnet-only simulation

For a Linux staging host with Caddy and Tailscale but no public domain, use
`Caddyfile.tailnet` and the user services in `tools/public-session-directory`.
Copy `server.py` and `Caddyfile.tailnet` to
`~/.local/share/gamebuilder-session-directory/`, and copy
`gamebuilder-session-directory.service` and
`gamebuilder-session-directory-caddy.service` to `~/.config/systemd/user/`, then
run:

```text
systemctl --user daemon-reload
systemctl --user enable --now gamebuilder-session-directory.service gamebuilder-session-directory-caddy.service
tailscale serve --https=443 --set-path=/session-directory/ --bg --yes http://127.0.0.1:47801
```

The directory listens only on loopback. Tailscale terminates HTTPS and mounts
the Caddy proxy under `/session-directory/`; the command preserves other Serve
routes. Adjust the Caddy service `ExecStart` if Caddy is installed somewhere
other than `~/.local/bin/caddy`. Set
`GAMEBUILDER_SESSION_DIRECTORY=https://<node>.<tailnet>.ts.net/session-directory`
for hosts and clients, or pass the same value as `--session-directory=`.
This endpoint is accessible only to devices in the tailnet, not the public
Internet. Do not enable Funnel on a port whose other Serve routes proxy unrelated
applications.

##### Stable public endpoint with Tailscale Funnel (free)

[Tailscale Funnel](https://tailscale.com/docs/features/tailscale-funnel) can
give the directory a stable public HTTPS URL using your own always-on Chopper
node and its `*.ts.net` hostname; it does not provide
always-on hosting if that node is offline. Funnel requires MagicDNS, HTTPS
certificates, Tailscale Funnel permission enabled for the node in the tailnet
policy, and admin approval where required. Funnel is a Tailscale beta feature;
the service is subject to Tailscale's non-configurable bandwidth limits.

The session directory must bind only to `127.0.0.1`; the updated service unit
enables `--accept-proxy-protocol-v2`. Funnel supplies the actual client IP in
its PROXY header, which takes precedence over `X-Forwarded-For`; ordinary local
HTTP from the existing Caddy routes remains supported. Deploy the updated
`server.py` to `~/.local/share/gamebuilder-session-directory/` and the updated
service unit to `~/.config/systemd/user/`, then run:

```text
systemctl --user daemon-reload
systemctl --user restart gamebuilder-session-directory.service
tailscale serve status
tailscale funnel --bg --proxy-protocol=2 --tls-terminated-tcp=8443 tcp://127.0.0.1:47800
tailscale funnel status
```

Use a free Funnel port not already serving another application; this setup
uses `8443`, leaving the existing tailnet-only Serve routes on `443` unchanged.
The public URL is `https://<node>.<tailnet>.ts.net:8443`; confirm it with
`tailscale funnel status`, then set `GAMEBUILDER_SESSION_DIRECTORY` to that URL
for hosts and clients or pass the matching `--session-directory=` argument.
Funnel terminates HTTPS and forwards only to the directory's loopback listener.
It does not expose Caddy's other routes. The API is unauthenticated, so anyone
can list public sessions and submit registrations subject to the directory's
rate and capacity limits.

The endpoint carries session-directory HTTPS only. Game traffic remains direct
UDP and still depends on IPv6 reachability, UPnP, or manual router forwarding.
Funnel does not relay or make player-hosted game servers reachable through
otherwise-blocked NAT.


##### Temporary public test with Cloudflare Quick Tunnel

Cloudflare [Quick Tunnels](https://developers.cloudflare.com/tunnel/get-started/quick-tunnels/)
need no account or domain and create a temporary
`https://<name>.trycloudflare.com` hostname. Install `cloudflared` at
`~/.local/bin/cloudflared`, copy
`gamebuilder-session-directory-cloudflared.service` to
`~/.config/systemd/user/`, then run:

```text
systemctl --user daemon-reload
systemctl --user enable --now gamebuilder-session-directory.service gamebuilder-session-directory-caddy.service gamebuilder-session-directory-cloudflared.service
journalctl --user -u gamebuilder-session-directory-cloudflared.service -n 50 --no-pager
```

Set `GAMEBUILDER_SESSION_DIRECTORY` for hosts and clients to the
`https://<name>.trycloudflare.com` URL printed by `cloudflared`, or pass it as
`--session-directory=`. The tunnel forwards only to loopback Caddy port `47802`;
Caddy requires Cloudflare's `CF-Connecting-IP` and replaces
`X-Forwarded-For` with that address before forwarding to the directory. The
tailnet route and other Chopper services are not exposed by this tunnel.

Anyone with the Quick Tunnel URL can access the unauthenticated directory. The
URL changes whenever a tunnel is created, stops working when the process stops,
has no uptime guarantee, and supports at most 200 in-flight requests. This is a
public smoke-test path, not durable hosting. It carries directory HTTP requests
only; game traffic remains direct UDP and still depends on IPv6 reachability,
UPnP, or manual router forwarding.
The service stores session records in memory and refreshes host data every 15
seconds. Public listings hide entries after 60 seconds without a heartbeat;
records remain migration-accessible for five minutes before purge. The
repository does not provide a centrally operated always-on directory. Tailscale
Funnel can provide a stable hostname through your own always-on Chopper node
(see above); the Compose setup automates TLS certificate provisioning but
requires a reachable domain and open ports. The directory limits each source IP
to eight new registrations per five minutes and supports up to 1,000
simultaneous sessions. It does not authenticate players. Direct UDP game
traffic is encrypted with Unity Transport's built-in DTLS and pinned server
certificates; directory requests require HTTPS.

The host sends an initial project snapshot (maximum 400 KiB), split into 48 KiB
reliable chunks to stay below NGO's 64,000-byte per-message limit. The direct
adapter forwards actor-state and transform RPCs, behavior messages (including
their sender), terrain edits, player identity and poses, ownership changes, and
observable player state (tint, visibility, avatar animation, and tool-effect
data). LAN/DNS/loopback smokes exercise host discovery, direct joins, identity,
RPC targeting, map switching, and kicking.
A transport client becomes a gameplay player only after its identity handshake
completes. A disconnect before that releases the assigned slot silently and
does not emit `PlayerLeft`.

Three independent Unity player processes on DDJARIN passed the three-player ownership-chain smoke over both LAN (`192.168.1.77`) and Tailscale (`100.86.44.23`): `PASS|owner=3|hostLocal=false|client2Kinematic=true|client3Kinematic=false`. This validates separate network paths on one machine, not cross-device reachability.
Standalone loopback tests transfer a 400 KiB initial snapshot and a 256 KiB
Photon RPC through live NGO/UTP managers. Client-originated `AllViaServer` RPCs
execute on the host and echo back to their origin in host-relayed order. Reliable
RPC, event, and observable packets are split into 48 KiB chunks, with a 1 MiB
packet cap. The temporary
Quick Tunnel smoke verified public HTTPS directory health, client-IP forwarding
against spoofed `X-Forwarded-For`, and register/list/heartbeat/unregister. Internet
UDP game reachability and router-specific forwarding remain untested; offline
smoke verifies save/load round-tripping.

If a direct-host session loses its host after a client loads the initial
snapshot, every client autosaves its local copy. When the self-hosted directory
is configured and reachable, the lowest connected player ID deterministically
starts a replacement host. It can claim the session after the old host heartbeat
has been stale for 30 seconds; remaining clients poll the authenticated private
or public session record and rejoin the new endpoint using its new certificate
pin. Recovery clients use the migrated directory address and port; they do not
bind a second host. The new host serves its local autosave, so changes not
present in that player's copy cannot be recovered. The replacement host retries
while the directory reports the previous host as active; invalid-request (400)
or expired-session (404) rejections end automatic migration. Network failures
remain retryable. If migration is unavailable or times out, the saved local
copy remains recoverable offline.

Direct-host mode implements the core player-hosted flow without PUN or paid
Unity networking plugins; it does not include every PUN/cloud feature. The
transport accepts at most 16 players (host plus 15 clients); the host assigns
reusable player IDs so disconnect/rejoin cycles do not exhaust slots. IPv4 and
IPv6-only hosting are separate choices. IPv6 hosts use IPv6 multicast for LAN
discovery and may appear in the public directory when configured and reachable
over IPv6. Each direct host keeps an ephemeral 2048-bit RSA private key in
memory and advertises only its public certificate. LAN discovery and the public
directory carry that certificate. Manual sharing uses a complete
`GBDIRECT1|address|port|certificate` invite. When the directory is reachable,
copied invites use its request-observed address. Public IPv4 addresses use the
selected UPnP external port; private/LAN addresses use the listening port.
The configured UDP port is the transport/UPnP internal port; the directory and
copied public IPv4 invites advertise the UPnP external port when mapping succeeds.
IPv6 advertises its configured UDP port directly.
Without the directory, the host falls back to a local interface address and
listening port, which may only be reachable on the LAN. The invite itself does
not require the directory.
Direct-host sessions show join/leave notices to the host and existing clients.
When a peer leaves, every surviving participant also receives the `PlayerLeft`
behavior event; a newcomer does not get join notices for the existing roster.
Clients reject raw `IP:port` joins instead of falling back to plaintext. The
pinned host certificate authenticates the server and encrypts transport. Hosts
may require an 8–128-byte shared password; clients without it are denied, but
this does not establish individual account identities. With no password, anyone
with an invite or public listing may attempt to join. Player names remain
client-supplied. Internet/router-specific reachability remains unverified.


* Local file and folder picker

    Local import/export features use Game Builder's built-in cross-platform picker
    for files, folders, and saving. It supports directory navigation, extension
    filters, multi-file selection (including batch sound imports), and explicit
    overwrite confirmation. No Crosstales plugin or platform-native file-dialog
    package is required.

* Card detail dialogs

    Card details fade and shrink when dismissed with the close button or
    Escape. The fade stops accepting input immediately; editor context changes
    close the dialog instantly to avoid leaving an overlay over the next view.

* Web image search

    The Creation Library searches Wikimedia Commons directly; no API key,
    paid Unity package, account, or Google Custom Search configuration is
    required. Search needs an internet connection.
    Wikimedia Commons does not provide an equivalent to Google's SafeSearch
    filter through this API; review images before adding them to a game.

    Results are limited to JPEG/PNG files marked Public domain, CC0, or a
    Creative Commons Attribution (CC BY) license. ShareAlike, NonCommercial,
    NoDerivatives, and other licenses are excluded. CC BY results without
    creator/credit metadata are excluded rather than shown without attribution.
    Selected images keep the Commons file page, creator, and license details
    in the saved image URI; those details appear in the game's procedural
    credits. Legacy image URIs remain loadable and continue to show their URL
    in the credits.

    Search uses the public Wikimedia Commons API and follows its
    [API etiquette](https://www.mediawiki.org/wiki/API:Etiquette). Images are
    hosted by Commons and can become unavailable if the source is removed.


## Existing Poly URIs

Google Poly is retired. Existing projects can still deserialize `poly:`
renderable URIs, but those remote models cannot be fetched and show a
conspicuous missing-asset placeholder. Replace them with a local OBJ using
the `renderuri` command above. No Poly API key or SDK setup is available.

## Optional integrations

* Steam Workshop uses Steamworks.NET (`com.rlabrecque.steamworks.net`, MIT),
  pinned in `Packages/manifest.json`; it replaces the former LapinerTools code.
* `USE_STEAMWORKS` is enabled for Standalone builds (Windows, macOS, Linux).
* Steam Workshop requires the Steam client, a logged-in account, and a valid
  App ID. For local development, create `steam_appid.txt` next to the executable
  with that App ID; keep the file out of version control.
* `WorkshopItemInfo.xml` keeps LapinerTools' XML schema (`PublishedFileId`,
  `IconFileName`, and `Tags/string`) and is written before upload, preserving the
  published ID in the content folder for later updates.
* If an existing `WorkshopItemInfo.xml` has no readable published ID, its
  upload stops rather than silently creating a duplicate Workshop item.
* Workshop queries default to fresh Steam results (`IsSteamCacheEnabled` is
  false); set it to opt into Steam's cached responses. Tag searches default to
  matching any required tag (`SearchMatchAnyTag`), consistent with LapinerTools.
* Subscribing requests downloads for missing or outdated items. Import and play
  actions wait until the item is installed, not downloading, and current.
* Remix opens an installed Workshop game in Build mode as a local project.
  Its Workshop ID is cleared, so saving creates a separate local bundle instead
  of editing the original Workshop item.

* When Steam requires Workshop legal-agreement acceptance, uploading opens the
  agreement page and returns an explicit error; accept it, then retry.
* Upload progress follows LapinerTools' preparation, content, preview, and commit
  stage weights instead of restarting at each Steam upload phase.
* Workshop results include Steam favorite counts and the user's favorite state.
  The adapter exposes add/remove favorite callbacks and camera-to-PNG icon capture.


* Without Steam initialization, Workshop search and upload are unavailable.
  The Workshop menus stop loading for unavailable and error results, and tolerate
  missing optional loading and empty-state UI references. Without Steamworks
  compile symbols, the sort controls use a name-order fallback (A–Z / Z–A).
  The saved local Workshop asset cache still works. Editor builds retain the
  disk-backed local Workshop source. Local model import needs no TriLib.
* `WorkshopMenuCallbackTests` covers no-Steam and failed-query feedback across
  the game, actor-prefab, and card-pack lists; it also tests late results after
  the main Workshop menu closes. `WorkshopMenuNoSteamSortTests` covers the
  fallback sort labels for all three lists.


## Archival Build

The `builds` directory contains the last build published to Steam
(`gamebuilder-ARCHIVE.7z.*`). It predates the plugin-free 2D renderer and
includes optional integrations such as Shaper-based 2D UI and the rich JS
editor.

`gamebuilder-OSS.7z` is a runtime build, not a source archive. The Steam
archive's compiled `Assembly-CSharp.dll` contains the former `SteamManager`,
`Steamworks`, and `LapinerTools` integration; it does not establish the source
contract used by this replacement.
The missing component GUID `62efa415090464f4d83ec1b8a5a5fadc` on `MC` was only a
plausible Workshop bootstrap; the archived binary did not map it to a class.
The orphaned missing-script entry has been removed from `Assets/Prefabs/MC.prefab`.
The current Standalone build uses Steamworks.NET for UGC and Steam identity;
the Steam client and a valid App ID remain runtime prerequisites.

## License

See the LICENSE file for the project license. Third-party notices remain applicable; the asset inventory links to the evidence found for each file.

## Contributing

This fork is independently maintained. Pull requests should target the branch they are intended to change: `unity` or `godot`.

This project is no longer actively developed or maintained.

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

* [`unity`](https://github.com/rufl/GBNG/tree/unity) preserves the original Unity project, pinned to Unity 6000.6.6f1; keep changes limited to compatibility, quality-of-life, and stability work.
* [`godot`](https://github.com/rufl/GBNG/tree/godot) is the GBNG — New Gen rewrite, targeting Godot 4.7.2 .NET, .NET 10, and ClearScript/V8 7.5.1.1 while reusing the original `.voos` projects and assets.

## Prerequisites

* Unity Editor 6000.6.6f1. This fork is pinned to that editor release.
* Unity's C# compiler (C# 9.0 language level). Unity controls the language version; use only the features supported by its compiler.
* [Python 3.15.2](https://www.python.org/downloads/release/python-3152/) for code generation. `.python-version` records the selected version.
* Unity packages: Test Framework 1.8.0, UGUI 2.6.0, and Post Processing 3.5.4. TextMesh Pro functionality is supplied by UGUI in this editor.
* Unity 6 no longer supplies the legacy UNet serializer. `VoosBinaryWriter` and `VoosBinaryReader` now own the existing little-endian game-state format instead of depending on the unsupported HLAPI package.
* The embedded V8 runtime remains a prebuilt native plugin. Its engine source and external build dependencies are not in this repository, so updating that binary requires those upstream inputs and native ABI validation.
* On Windows, Git Bash (included with [Git for Windows](https://gitforwindows.org/)) to run shell tools. On macOS, use Terminal.

## Project layout

* `Assets/` — Unity scenes, scripts, prefabs, art, audio, and runtime resources. Keep Unity asset paths and `.meta` GUIDs intact.
* `Packages/` — Unity Package Manager dependencies.
* `ProjectSettings/` — pinned Unity editor version and project settings.
* `tools/codegen/` — Python generators and the `gen-all.sh` entry point.
* `tools/integrations/` — optional Photon, Steamworks, and embedded-browser setup and patches.
* `tools/public-session-directory/` — dependency-free service for a self-hosted public player-session list.
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


## Building the standalone EXE

To build a standalone, you *must* use the menu item `Game Builder
| Build`. Going through the normal Unity Build Settings window will *not* work.
Once done, your build will be located under the `build_output` folder.

We have included a reference build (`builds/gamebuilder-OSS.7z`). This is the
build that you should get if you simply build a Windows standalone as-is,
without enabling any extra plugins.

## Scripting documentation

For documentation about how the in-game scripting works, open the `docs.html`
file in a browser (after downloading/cloning this repository).

## Asset and license inventory

Regenerate the catalog with `python3 tools/asset-inventory/generate.py`. Serve the repository root with `python3 -m http.server 8000`, then open `http://localhost:8000/tools/asset-inventory/`.

The catalog lists license evidence and marks files without a located notice as unidentified; third-party terms remain separate from the project license.

# Optional integrations

The `unity` branch runs without Unity Asset Store plugins. Built-in 2D drawing,
local OBJ/FBX model loading, the built-in and saved actor libraries, and in-app
project saving are part of the baseline. Optional integrations below may
require separate plugins, accounts, or API keys.

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
   without TriLib. Missing textures fall back to material color. Animation clips
   loop from the first clip; mesh and sidecar files remain external and are not
   embedded in a saved project.

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

Ground materials using shaders without recognized tint properties keep their
authored color instead of aborting scene load.

## Behavior code editor

The full Monaco behavior editor is supported in Windows x64 players using
Direct3D 11 or 12. It uses the MIT-licensed WebViewToolkit, vendored Monaco
and Microsoft's free [Evergreen WebView2 Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/);
no paid Unity plugin is required.

It provides syntax highlighting, completions, behavior-system typings and
source navigation, editing, read-only mode, diagnostics, scroll restoration
and zoom. If WebView2 is missing, the graphics API is unsupported, or the
build targets another platform, the built-in multiline editor remains
available.
The native browser surface is created lazily at the visible code panel's pixel
dimensions. Native resizing is avoided because the bundled WebView resize path
can crash; Unity scales the existing texture if the window layout later changes.

## Optional integrations available for free

### Multiplayer: player-hosted direct connections

The Unity path uses Unity Netcode for GameObjects and Unity Transport; neither
Photon PUN nor a paid Unity service is required.

- From the splash screen, open **Multiplayer** and choose **Host a game** for
  the unchanged IPv4 host mode. Choose **Host IPv6 game** for an IPv6-only host.
- Other players on the same IPv4 LAN see IPv4 hosts in the room list
  automatically. Use **Join by address** with an IPv4/IPv6 address or DNS
  hostname (`:7777` optional) if discovery is blocked. Use `[IPv6]:port` to
  specify a custom port for an IPv6 literal; DNS lookup prefers IPv4 and falls
  back to IPv6. The multiplayer panel shows the host's share address.
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

- A self-hosted public session directory can provide a global room list. Only
  hosts started with **Public Multiplayer** register their name and player
  count; private sessions remain available through local discovery or a shared
  address but do not appear in the public list. The directory learns a public
  IPv4 or IPv6 address from the request source and rejects a source family that
  does not match the advertised game transport. This is discovery only, not a
  relay or NAT traversal service.

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
server and inbound TCP ports `80` and `443` reach it. Only Caddy publishes
ports; the directory API stays on the private Docker network. Set
`GAMEBUILDER_SESSION_DIRECTORY=https://<DIRECTORY_DOMAIN>` for every host and
browser, or pass the matching `--session-directory=` argument. Change
`DIRECTORY_SUBNET`, `DIRECTORY_PROXY_IP`, and `DIRECTORY_SERVICE_IP` in `.env`
if the default Docker subnet conflicts with another network.

The service stores active sessions in memory, refreshes host data every 15
seconds, and expires entries after 60 seconds without a heartbeat. The free
software does not include hosting, a public hostname, or a reliable always-on
Internet endpoint. The Compose setup automates TLS certificate provisioning,
but requires a reachable domain and open ports. The directory limits each
source IP to eight new registrations per five minutes and supports up to 1,000
simultaneous sessions. It does not authenticate players or encrypt the game
transport.

The host sends an initial project snapshot (maximum 400 KiB). The direct
adapter forwards actor-state and transform RPCs, terrain edits, player identity
and poses, ownership changes, and observable player state (tint, visibility,
avatar animation, and tool-effect data). LAN/DNS/loopback smokes exercise host
discovery, direct joins, identity, RPC targeting, map switching, and kicking;
offline smoke verifies save/load round-tripping. Internet reachability and
router-specific forwarding are untested.

Direct-host mode implements the core player-hosted flow, not every PUN/cloud
feature. Host migration is unavailable. The transport accepts at most 16
players (host plus 15 clients); the host assigns reusable player IDs so
disconnect/rejoin cycles do not exhaust player slots. IPv4 and IPv6-only hosting
are available as separate choices. IPv6 hosts are discovered by IPv6 multicast
on the same LAN and may appear in the public session directory when configured
and reachable over IPv6; manual address sharing remains available.
Direct connections have no application-level authentication or encryption;
share the host address only with trusted players. Manual address joins work
without the directory.


* [Crosstales File Browser](https://assetstore.unity.com/packages/tools/utilities/file-browser-standalone-98716) - Enables the system file browser UI for local disk import/export features

    - Import the plugin from the Asset Store link above.
    - Define the directive `USE_FILEBROWSER`.


* Google Image Search

    To enable Google Image Search in you Creation Library, you must setup a Custom Search Engine on Google Cloud Platform.

    (Instructions assume basic familiarity with Google Cloud)
    - Create a new project on Google Cloud Platform
    - Enable the “Custom Search API”
    - Create an API Key for the Custom Search API (via “Create Credentials”) 
    - Inside the Unity project, paste the generated API key in to the `APIkey` variable at the top of GisSearchManager.cs
    - Go to the Custom Search Engine control panel (cse.google.com) and create a new search engine. When creating the search engine, enter any domain (we will delete it in the next step).
    - Edit the new search engine
      - Remove the site to search
      - Turn on “search the entire web”
      - Turn on “image search” and “safe search”
      - Inside the Unity project, copy the search engine's ID and paste it in to the searchID variable at the top of GisSearchManager.cs
    - You are all set!


## Existing Poly URIs

Google Poly is retired. Existing projects can still deserialize `poly:`
renderable URIs, but those remote models cannot be fetched and show a
conspicuous missing-asset placeholder. Replace them with a local OBJ using
the `renderuri` command above. No Poly API key or SDK setup is available.

## Optional integrations that still need plugins

* Steam Workshop requires the Steam Workshop integration. Local model import
  uses the free runtime importer and does not require TriLib.


## Archival Build

The `builds` directory contains the last build published to Steam
(`gamebuilder-ARCHIVE.7z.*`). It predates the plugin-free 2D renderer and
includes optional integrations such as Shaper-based 2D UI and the rich JS
editor.

## License

See the LICENSE file for the project license. Third-party notices remain applicable; the asset inventory links to the evidence found for each file.

## Contributing

This repository does not currently accept pull requests, but
you are welcome to fork it to make your own version of it, if you
want to.

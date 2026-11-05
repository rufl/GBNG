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

This fork keeps the two implementations separate:

* `unity` preserves the original Unity project and stabilizes it on the pinned Unity 6 stack. Keep changes there limited to compatibility, quality-of-life, and stability work.
* `godot` is the GBNG — New Gen rewrite. It targets Godot 4.7.2 .NET, .NET 10, and ClearScript/V8 7.5.1.1; it reuses the original `.voos` projects and assets while replacing Unity runtime services with Godot implementations.

Install the Godot 4.7.2 .NET build and .NET 10 SDK, then run `dotnet build GBNG.Godot.sln`. On Windows, use the PowerShell launcher below: it follows package-manager links to the real executable, avoiding a WinGet Mono startup stall. On Linux and macOS, launch with the installed `godot` executable.

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\tools\godot\run.ps1 --path . -- res://Assets/StreamingAssets/ExampleGames/Public/sample-platformer.voos --play
```

To exercise terrain editing, selection, copy/paste, overwrite, undo/redo, and cancellation in the Godot smoke harness:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\tools\godot\run.ps1 --path . -- res://Assets/StreamingAssets/ExampleGames/Public/sample-platformer.voos --smoke-terrain
```

The smoke prints `TERRAIN_COPY_SMOKE_OK` and `TERRAIN_EDITOR_SMOKE_OK` on success and writes screenshots under `.godot/`.

To open the editor without a bundled example, run `pwsh -NoProfile -ExecutionPolicy Bypass -File .\tools\godot\run.ps1 --path .` on Windows, or `godot --path .` on other platforms.

Bundled examples are read-only project resources; use **Save as…** to create a writable `.voos` copy.

The Godot branch loads and saves legacy `.voos` projects, supports actor selection and inspector editing (including behavior-brain assignment), and provides behavior authoring for embedded JavaScript and legacy typed properties using Godot's native code editor. Its first-party offline asset store searches the generated built-in renderable catalog and models or images imported into the project's `<project>.assets/` sidecar; it previews converted 3D models and images, and imports local `.glb`, `.gltf`, or `.fbx` models plus `.png`, `.jpg`, `.jpeg`, and `.webp` images. Imported images are available to legacy `propImage` properties and render through `uiImage` and `uiImageSlice` without remote lookups. The store needs no account or remote asset service, and Save As carries imported references and files to the new project location. GBNG does not verify licenses for user-imported files; users must check source terms before reuse or sharing.

The legacy behavior UI uses the original ProggyCleanTT BMFont metrics and atlas for text and button labels, preserving fixed-width glyph advances across platforms. Godot loads the bitmap font natively; its assets are MIT-licensed under `Assets/third_party/Fonts/ProggyClean/LICENSE.txt`.

The **Copy** toolbar action (or Ctrl+C) previews the selected actor and its transform descendants. Move the cursor and click to place copies; hold Ctrl/Cmd to snap to the legacy grid, click again to repeat, or press Esc to cancel. Copies preserve actor data, behavior, clone lineage, transforms, and hierarchy. Each placement is one Undo/Redo step and follows the collaborative-edit history path.
The actor list and world support multi-selection: Shift adds, Ctrl/Cmd toggles, and dragging a marquee selects actors by their projected render-bounds centers. **Ctrl+L** / **Cmd+L** or the actor-list **Hide/Show** button collapses and restores the list without changing selection. **Delete** or **Backspace** removes selected actors as one undoable collaborative edit; Ctrl+C copies all selected roots and their transform descendants. Press **R** or **Rotate 45°** to rotate the selection about its average position in one undoable collaborative edit. Press **Ctrl+R** to restore all actors from their stored project transform and velocity state as one undoable edit. **Ctrl+S** / **Cmd+S** saves the current project, prompting for a writable copy when the open example is read-only. Press **F** or **Follow** to keep the camera centered on the focused actor; toggle again to stop. Press **Ctrl+H** or **Cmd+H** with one actor selected to parent it to the actor under the pointer; empty world space unparents it. The inspector's **Transform parent** control provides the same assignment and clear behavior through a scene picker. With **Parent on reset** enabled, either route changes the reset parent; parenting preserves world transform and motion, rejects hierarchy cycles, and records one collaborative Undo/Redo edit.

The **Parent on reset** checkbox controls whether **Ctrl+H** / **Cmd+H** and the inspector's **Transform parent** picker also change the parent restored by reset; it is enabled by default and saved as an editor preference on this device.

The **Update spawn: Move / Rotate** preferences are enabled by default and saved on this device. When enabled, move and rotation edits update restart position and rotation for the entire actor family; scaling always refreshes family restart transforms. These changes participate in Undo/Redo and collaborative edits.

The inspector's **Spawn position** fields edit the saved restart position independently of the actor's current world transform and spawn rotation; each edit participates in Undo/Redo. **Use current position** copies the actor's current world position and transform parent into its restart state without changing its current transform or spawn rotation.

The inspector's **Reset parent** control restores the separate legacy **Parent on restart** action: press **Choose in scene…**, click an actor to assign it, or click empty world space to clear it. It changes `spawnTransformParent` without changing the actor's current transform parent and participates in Undo/Redo.

The inspector's **Show offsets (advanced)** checkbox exposes model position and rotation offsets. Editing them affects only the rendered model, not the actor or restart transforms, and supports Undo/Redo. The controls are disabled during play and multi-selection.


The **World** editor sets sky type (Day, Space, Overcast, or Solid color), sky color, and scene lighting (Day, Night, or Dark). Changes update the live scene, persist in the original `.voos` stage data, and participate in Undo/Redo and host-authoritative collaborative edits.

The **World** settings also provide the original map-size presets: Small (50×50), Medium (200×200), Large (500×500), and Long (50×5000). Resizing preserves terrain cells in the centered overlap, warns before shrinking discards out-of-bounds terrain, and updates flat ground when no terrain payload exists. The resize is one undoable edit, persists in `.voos`, and uses the same host-authoritative collaboration flow as other world settings.

The **Effects** toolbar opens seven temporary camera filters: Pixelate, Film, B&W, Saturate, VHS, Krazy, and Drawing. Their Godot-native shader equivalents can be stacked and do not change or save into `.voos` projects. The original CameraFilterPack shader sources are not included, so these restore each filter's visual intent rather than pixel-identical output.

Behavior UI image and image-slice widgets honor the legacy `noFilter` option: nearest sampling when enabled, bilinear sampling when omitted or false. Behavior UI `BORDER` and `DASHED` circles select 3–300 segments from their projected screen radius, matching the legacy renderer's adaptive detail.

Behavior UI button hitboxes include their top and left edges and exclude their right and bottom edges, matching Unity's half-open rectangle containment.
Behavior UI text and button labels preserve fractional legacy `textSize` values instead of rounding them to integer font sizes.

The **Options** toolbar button controls device-local fullscreen (windowed by default) and selectable windowed-size presets constrained to the monitor's usable area. It shows the current size; fullscreen uses the monitor's current display mode and disables windowed-size selection. Display settings save to `user://editor-settings.cfg`. Graphics quality defaults to High and persists locally: High enables SSAO, four directional-shadow splits, up to 4× anisotropic filtering for mipmapped 3D materials, and GPU motion-vector blur for opaque 3D geometry on desktop Forward+; Medium disables SSAO and motion blur, uses two shadow splits with a 40-unit distance, and retains 4× filtering; Low disables SSAO, shadows, anisotropic filtering, and motion blur, then applies a +1 mipmap bias so mipmapped textures sample one level coarser (softer appearance, no reduction in texture memory). All profiles leave VSync enabled, matching Unity. Mipmap bias works in the configured Forward+ and Mobile renderers; Compatibility ignores it. Motion blur leaves transparency and editor UI unblurred and is unavailable on Mobile and Compatibility renderers. The persistent **Show editor help tooltips** option (enabled by default) and camera input controls include invert-vertical mouse look, mouse-look sensitivity (0.1–3×), and mouse-wheel zoom sensitivity. Help tooltips can be hidden across existing and newly created UI controls; code-diagnostic hovers and player action prompts remain visible. Camera controls reset to platform defaults. None of these settings are stored in `.voos` projects.
The **Audio** toolbar button controls device-local sound-effects volume (0–100%, default 50%) and background music volume (0–100%, default 100%) on separate mixer buses; both save to `user://editor-settings.cfg`. Godot plays the repository's original music tracks using the Unity A–F adaptive stem progression and `AudioStreamInteractive` transitions scheduled at each clip's end.

When the main window loses focus, the app caps its frame rate at 30 FPS to reduce background load; regaining focus restores unlimited foreground rendering.

In play mode, legacy player tooltips for `Action1` and `Action2` render as bottom-center LMB/RMB prompts for the local player. LAN hosts forward each player's prompts to the matching client; stopping play clears them.

The visual card library supports text/category filtering, card-icon previews, hidden-card filtering, and drag-and-drop deck assignment. The canvas edits panels, card uses, notes, and panel layout while preserving legacy `.voos` references. A card manager creates, copies, edits, and deletes embedded cards and imports/exports legacy card-pack folders; conflicting IDs can be overwritten, duplicated, or canceled.

Terrain authoring supports the legacy create, dig, and paint brushes plus drag selection on the original `.voos` grid. Copy selection shows a translucent cell preview; paste snaps from a deterministic minimum-cell anchor, keeps the source intact, and supports overwriting with Undo/Redo. Escape cancels placement. Brush strokes and copy/paste are undoable, and terrain payloads persist in the original project format. The editor also provides bounded 50-step Undo/Redo for actor property changes, creation/deletion, and reset. The runtime includes single-player host services, Godot solid/trigger physics, audio, particles, behavior UI, and legacy keyboard/mouse/message events. LAN rooms use ENet host/join, UDP discovery, an eight-player roster, and bounded `.voos` plus asset-sidecar transfer. Connected rooms support host-authoritative collaborative edits: clients submit document operations, the host applies and relays accepted changes, and same-field conflicts resolve in host-acceptance order. New players receive a current document snapshot and imported-model sidecars. Hosts can import models during a session; clients can import into the shared project too, with the host merging and rebroadcasting the asset bundle. A client's import is acknowledged after the shared assets have been applied; wait for sync before creating an actor that uses a newly imported model. On writable projects, assets are saved to the project's `.assets` sidecar. If the host opened a bundled read-only example, imported assets remain in local app data until **Save as…** creates a writable project.

Gameplay snapshots are compressed and sent in bounded 1 KiB unreliable-ordered chunks (16 MiB maximum). Clients apply only complete snapshots and discard incomplete transfers when a newer snapshot supersedes them.

This is not yet full parity. Internet invite-code hosting uses peer-to-peer WebRTC with optional coturn relay and requires an operator-run matchmaking/signaling service; LAN hosting and editing need no service or Docker. There is no bundled public matchmaking service, user accounts, parental controls, player-identity verification, moderation, or persistent rooms. Invite codes are bearer credentials; WebRTC data channels use DTLS encryption, while the signaling service sees SDP/ICE and source-IP metadata. See `tools/networking/README.md` for optional self-hosted matchmaking and relay tooling. The Godot asset store is an offline replacement for Poly; Google Image Search is disabled. Native CodeEdit replaces Monaco's browser for behavior and managed-card JavaScript editing with JavaScript syntax highlighting, line numbers, automatic indentation/braces, API suggestions from `VISIBLE_TO_MONACO` declarations (open suggestions with Ctrl+Space), line-numbered syntax diagnostics on Apply, and runtime error diagnostics in the open behavior editor with source lines. The editor also remembers vertical scroll position per behavior while switching assigned uses. The behavior and managed-card editors provide A−, A+, and Reset zoom controls, with font sizes clamped to 8–36 points. Both editors show a red `!` gutter marker and tinted source line for syntax diagnostics; the behavior editor also marks runtime errors. Hovering a marked line shows its diagnostic text. The behavior editor draws red squiggle underlines at reported syntax and runtime columns when available. Invalid managed-card JavaScript is not applied, preserving its last valid source. The optional Unity 2D Shaper plugin itself is not carried over; Godot implements the legacy behavior-UI drawing primitives natively. Several Unity-specific editor workflows remain unmigrated. The `unity` branch remains the complete reference for those contracts. The asset-license inventory covers repository files and their located license evidence; it does not verify licenses for models imported into a user's `.assets` sidecar.

## Prerequisites

* [Unity Editor 6000.6.6f1](https://unity.com/releases/editor/whats-new/6000.6.6f1). This fork is pinned to that editor release.
* Unity's C# compiler (C# 9.0 language level). Unity controls the language version; use only the features supported by its compiler.
* [Python 3.15.2](https://www.python.org/downloads/release/python-3152/) for code generation. `.python-version` records the selected version.
* Unity packages: Test Framework 1.8.0, UGUI 2.6.0, and Post Processing 3.5.4. TextMesh Pro functionality is supplied by UGUI in this editor.
* Unity 6 no longer supplies the legacy UNet serializer. `VoosBinaryWriter` and `VoosBinaryReader` now own the existing little-endian game-state format instead of depending on the unsupported HLAPI package.
* The embedded V8 runtime remains a prebuilt native plugin. Its engine source and external build dependencies are not in this repository, so updating that binary requires those upstream inputs and native ABI validation.
* On Windows, Git Bash (included with [Git for Windows](https://gitforwindows.org/)) to run shell tools. On macOS, use Terminal.

## Project layout

* `Godot/` — Godot-native application, behavior runtime, project persistence, world simulation, and converted-asset support.
* `project.godot`, `GBNG.Godot.sln`, and `GBNG.Godot.csproj` — Godot project entry point and .NET build.
* `Assets/` — shared legacy Unity assets, behavior library, and `.voos` examples. Preserve Unity asset paths and `.meta` GUIDs on the `unity` branch; Godot reads converted resources and the legacy project formats on `godot`.
* `Packages/` and `ProjectSettings/` — Unity Package Manager dependencies and pinned Unity editor settings for `unity`.
* `tools/codegen/` — Python generators and the `gen-all.sh` entry point.
* `tools/integrations/` — optional Photon, Steamworks, and embedded-browser setup and patches for the Unity reference.
* `tools/licensing/`, `tools/documentation/`, and `tools/maintenance/` — license, documentation, and maintenance utilities.
* `tools/asset-inventory/` — generated visual asset and license catalog.
* `third_party/` and `Assets/third_party/` — vendored dependencies with their individual notices.
* `docs/` and `builds/` — generated scripting docs and historical build archives.

## Running code generation

Run `bash tools/codegen/gen-all.sh` from the repository root with Python 3.15.2. The generators update marked regions in Unity C# and JavaScript source files.

## Running in Unity Editor

1. Open the repository root with Unity Editor 6000.6.6f1.
1. Open the splash screen scene: `Assets/splash.unity`.
1. If Unity prompts for TextMesh Pro essential resources, import them and restart the editor.
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

# Optional features

Game Builder was originally built using several Unity Asset Store plugins. We
cannot include them in the repo, so by default the features that require them
are disabled. However, we've set things up so it's easy to re-enable some
features once you've purchased and imported some plugins (many are free!).
Instructions for individual features are below.

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

## Features that can be enabled for *free*

* [Photon Unity Networking](https://assetstore.unity.com/packages/tools/network/photon-unity-networking-classic-free-1786) - Enables multiplayer and collaborative-editing
    - NOTE: Because PUN integration is complex, we recommend you start with a
      *fresh* checkout of the codebase before following these steps. This
      reduces the likelihood of prefabs losing important property values after
      being edited without PUN.
    - First run the project as-is, freshly checked out, just to make sure it's working.
    - Don't forget to confirm TMPro's import dialog (import TMP Essentials).
    - Import the plugin from the Asset Store link above.
    - Define the directive `USE_PUN`
    - *Close* Unity before the next step
    - Run `bash tools/integrations/setup-photon.sh` from the repository root.
    - Sign up for a PUN account (https://www.photonengine.com/)
    - In the PUN website, create a new application (type: *Photon PUN*)
    - Copy your new application's *AppId*.
    - Open the Unity project again and wait for importing to finish
    - Enter your AppId if prompted, but if not, go to the next step (the dialog
      might freeze while Unity imports assets, but don't worry, just wait until
      it finishes).
    - Finally, inspect the "PhotonServerSettings" asset and change the following:
        - "Hosting" to "Best Region"
        - "Regions" to "Everything"
        - Paste your AppId into the "AppId" field

    - Making sure it's all working:
      - After the above steps, make a build (`Game Builder | Build`).
      - Run the build and create a private multiplayer game (`New Project | Empty | Private MP`).
      - Run another instance (using the same build copy is fine) and join that game (`Multiplayer | Use Join Code`).
      - Confirm that both "players" can run around as their robots (red and green), and that movements are seen by both.
      - Confirm that when one player scales an actor (like the text box), the other player sees the size change as well.
      - If these don't work, try this: In Unity's project window, inspect the
        Actor prefab. In its PhotonView component, does it have
        "ActorNetworking" as one of its "Observed components"? If it does not,
        then something went wrong, and you should start over with a fresh
        checkout.

* [Crosstales File Browser](https://assetstore.unity.com/packages/tools/utilities/file-browser-standalone-98716) - Enables the system file browser UI for local disk import/export features

    - Import the plugin from the Asset Store link above.
    - Define the directive `USE_FILEBROWSER`.

* Poly Toolkit for Unity

    To enable importing 3D models from [Poly](https://poly.google.com),
    you must enter an API Key for the Poly API.

    You do *NOT* need to import the Poly SDK (Poly Toolkit). It's already
    included. All you need to do is enter your API key.

    To do so, click the PtSettings.asset file in Unity (you will find it in
    Assets/third_party/PolyToolkit/Resources). The Inspector window will
    show several options for it. Click the RUNTIME tab, open the Auth Config
    section and enter your API Key, replacing the placeholder text
    "INSERT YOUR API KEY HERE".

    For instructions on how to get an API key, see the
    [Google Cloud Console](https://console.cloud.google.com/).
    When you create your Google Cloud
    Console project, don't forget to enable the Poly API on it *before*
    (not after) generating your API Key.

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


## Features that require premium plugins

* [2D Shaper Library](https://assetstore.unity.com/packages/tools/gui/2d-shaper-library-105973)

    If you want game-generated 2D graphics (that is, you want to allow games
    to show HUD text, draw shapes on the screen, etc).
    - Import the plugin from the Asset Store link above.
    - Define the directive USE_SHAPER_2D_LIBRARY. 
    
* [Steam Workshop](https://assetstore.unity.com/packages/tools/integration/steam-workshop-easy-steamworks-integration-86189) - If you are releasing on Steam and want to enable functionality like project sharing.
    - Import the plugin from the Asset Store link above.
    - Run `bash tools/integrations/setup-steamworks.sh` from the repository root.
    - Define the directive USE_STEAMWORKS.
    - Find the file "steam_appid.txt" in the repo root (NOT the one in "redist"). Put your AppID in there (instead of 480).
    
* [TriLib](https://assetstore.unity.com/packages/tools/modeling/trilib-model-loader-package-91777) - To import FBX files with animation from Poly.
    - Import the plugin from the Asset Store link above.
    - Define the directive USE_TRILIB.

* [Embedded Browser](https://assetstore.unity.com/packages/tools/gui/embedded-browser-55459)

    This plugin allows rendering of an HTML page inside of Game Builder,
    which enables you to use the Monaco-based code editor IDE.
    - Import the plugin from the Asset Store link above.
    - Define the directive `USE_ZFBROWSER`
    - Run `bash tools/integrations/setup-browser-assets.sh` from the repository root.

## Archival Build

In the `builds` directory, you will also find the last build that we published
to Steam (the `gamebuilder-ARCHIVE.7z.*` files). This differs from the open-source build
because it has several optional plugins enabled such as 2D UI, rich JS editor,
etc.

## License

See the LICENSE file for the project license. Third-party notices remain applicable; the asset inventory links to the evidence found for each file.

## Contributing

This repository does not currently accept pull requests, but
you are welcome to fork it to make your own version of it, if you
want to.

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

For the Godot branch, install the Godot 4.7.2 .NET build and .NET 10 SDK, then run `dotnet build GBNG.Godot.sln` and open the repository's `project.godot` in Godot. To launch a bundled example from the command line:

```sh
godot --path . -- res://Assets/StreamingAssets/ExampleGames/Public/sample-platformer.voos --play
```

Bundled examples are read-only project resources; use **Save as…** to create a writable `.voos` copy. The Godot branch currently loads and saves projects, supports actor selection and inspector editing (including behavior-brain assignment), and provides a behavior-authoring window to add/remove uses, edit embedded JavaScript, copy built-in modules into embedded scripts, and edit typed properties using the original behavior definitions, including conditional field visibility, scene-actor and actor-group selection, ordered actor/enum-array/card-deck editing, category-aware deck choices, and `oneCardOnly` enforcement. Raw legacy JSON remains available for advanced property-assignment edits. The editor also provides bounded 50-step Undo/Redo for actor property changes, creation/deletion, and reset. It instantiates converted assets and runs legacy behaviors with keyboard/mouse state and message events alongside Godot solid/trigger physics, editable terrain, audio, particles, behavior UI, and single-player host services for physics queries, player-control transfer, actor cloning/destruction, projection/screen info, and scene settings. Profiling markers do not collect timings yet. This is not feature-complete: the original visual card/panel graph editor, dedicated sound/image/prefab/particle asset pickers, multiplayer/Photon synchronization and room discovery, Steam Workshop publishing/assets, optional third-party integrations, and several Unity editor workflows remain unmigrated. Keep `unity` as the complete reference until those contracts have Godot replacements.

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

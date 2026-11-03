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

* [`unity`](https://github.com/rufl/GBNG/tree/unity) preserves the original Unity project, pinned to Unity 6000.6.7f1; keep changes limited to compatibility, quality-of-life, and stability work.
* [`godot`](https://github.com/rufl/GBNG/tree/godot) is the GBNG — New Gen rewrite, targeting Godot 4.7.2 .NET, .NET 10, and ClearScript/V8 7.5.1.1 while reusing the original `.voos` projects and assets.

## Prerequisites

* [Unity Editor 6000.6.7f1](https://unity.com/releases/editor/whats-new/6000.6.7f1). This fork is pinned to that editor release.
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
* `tools/licensing/`, `tools/documentation/`, and `tools/maintenance/` — license, documentation, and maintenance utilities.
* `tools/asset-inventory/` — generated visual asset and license catalog.
* `third_party/` and `Assets/third_party/` — vendored dependencies with their individual notices.
* `docs/` and `builds/` — generated scripting docs and historical build archives.

## Running code generation

Run `bash tools/codegen/gen-all.sh` from the repository root with Python 3.15.2. The generators update marked regions in Unity C# and JavaScript source files.

## Running in Unity Editor

1. Open the repository root with Unity Editor 6000.6.7f1.
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

## Behavior code editor

The Windows 64-bit build uses the free, MIT-licensed WebViewToolkit with the
free Microsoft Edge WebView2 runtime and the vendored Monaco editor. It
provides syntax highlighting, completions, behavior-system typings and source
navigation, code editing, read-only mode, error display, scroll restoration,
and zoom. Install the WebView2 runtime to use Monaco; if it is unavailable or
on other platforms, the editor falls back to the built-in multiline editor.

## Optional integrations available for free

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

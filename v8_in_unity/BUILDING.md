# Rebuilding the native V8 plugin

## What is in this repository

The Unity wrapper source, C ABI header, Visual Studio/Xcode projects, and native regression harness are in `v8_in_unity/`. The V8 engine source and its fetched third-party dependencies are not vendored here. The historical build note pins the engine to commit `d2e0166ded485df126c765b9196f7edd1ce50f82`; that revision reports V8 `7.1.0.0` in `include/v8-version.h`.

The commit is the recorded provenance of the bundled plugin, not a cryptographic proof of how the checked-in binaries were produced. V8's own `DEPS` file pins its dependency revisions, fetched by `gclient sync`. This procedure is reproducible from those pinned inputs, but it is not hermetic or offline: depot_tools, Visual Studio/Xcode, and the upstream V8/dependency sources remain external prerequisites. Compiler/toolchain differences can also change binary hashes.

The wrapper's C ABI is declared in `v8_in_unity/v8_in_unity.h` and consumed from C# in `Assets/Scripts/Voos/V8InUnity/Native.cs`. Do not update the engine commit or alter GN flags without running the native harness and a standalone Unity behavior-code smoke test.

## Windows x64

Requirements: Windows x64, depot_tools (`fetch`, `gclient`, `gn`, `ninja`), Visual Studio 2017 C++ tools (`v141`) and Windows SDK `10.0.17134.0`. Use the Visual Studio 2017 Developer Command Prompt so `msbuild` is on `PATH`.

From a directory outside this repository, fetch and sync the exact engine revision:

```bat
fetch v8
cd v8
git checkout d2e0166ded485df126c765b9196f7edd1ce50f82
gclient sync
```

Then, from the Game Builder repository root, build V8, build/run the native tests, and install the DLL only after the tests pass:

```bat
python tools\maintenance\build_v8_in_unity.py --v8-root C:\src\v8
```

The script checks the exact git revision, rejects tracked source edits, checks V8's `7.1.0.0` version header, generates `out.gn/x64.release` with a static monolithic x64 build and embedded startup data, builds `v8_monolith`, builds the wrapper and `v8_in_unity_test`, runs the test executable, and only then copies the resulting DLL into `Assets/Plugins/V8InUnity/`. `V8_ROOT` can replace `--v8-root`.

`v8_use_external_startup_data=false` is intentional: the shipped plugin has no separate V8 startup-data blobs under `StreamingAssets`; the native initialization/evaluation smoke succeeds with a nonexistent runtime-data directory.


## Linux x86_64

Requirements: Ubuntu 22.04 x86_64, depot_tools, Python 2.7 and Python 3, `ninja-build`, and the standard C++ build tools. The script creates a Python 2 shim under `out.gn/x64.release/maintenance-bin` for V8 7.1's legacy build helpers.

From a directory outside this repository, fetch and sync the exact engine revision and install its pinned sysroot/toolchain:

```sh
fetch v8
cd v8
git checkout --detach d2e0166ded485df126c765b9196f7edd1ce50f82
gclient sync --nohooks --revision v8@d2e0166ded485df126c765b9196f7edd1ce50f82
python2.7 build/linux/sysroot_scripts/install-sysroot.py --arch=x64
python2.7 tools/clang/scripts/update.py

export PATH="$DEPOT_TOOLS:$PATH"
python3 "$DEPOT_TOOLS/download_from_google_storage.py" \
  --no_resume --platform=linux --no_auth --bucket chromium-gn \
  -s buildtools/linux64/gn.sha1
```

From the Game Builder repository root, build V8, compile the Linux shared library and native regression harness, run the harness, and install the plugin only after it passes:

```sh
python3 tools/maintenance/build_v8_in_unity.py --v8-root /path/to/v8
```

Validated in Ubuntu 22.04 x86_64 under WSL2: the script builds the pinned V8 monolith, passes all 99 native regression checks, then loads `libv8_in_unity.so` and verifies initialization, `6 * 7 == 42`, and deinitialization before installation. The Unity 6000.6.6f1 Mono player smoke ran on Ubuntu 26.04 under WSL2: it loaded V8, evaluated `6 * 7 == 42`, and exercised `DirectHostDiscovery` without the former `GetIPv6Properties` exception; Linux uses libc `if_nametoindex` as the fallback. Linux standalone builds rewrite AssimpNet's four `libdl.so` imports to `libdl.so.2` and bundle `libminizip.so` beside Assimp, preloading it globally to satisfy `libassimp.so`'s `libminizip.so.1` dependency without `LD_LIBRARY_PATH`. The player smoke imported a one-triangle OBJ through Assimp. This proves native loading and mesh import, not the full FBX asset flow. See `Assets/StreamingAssets/third_party/Minizip-LICENSE.md` for the bundled Ubuntu package version, hash, and Zlib license. The Linux CEF/Monaco path was smoke-tested on Unity 6000.6.6f1 Mono under Ubuntu 26.04 WSL2/WSLg with Mesa llvmpipe: the page-ready signal arrived, the browser texture was 1280x720, and the editor JS bridge round-tripped code. Physical Linux hardware/GPU rendering and cross-host IPv6 multicast remain untested.

## macOS

The Xcode projects now use `V8_ROOT` and `V8_OUT` instead of the original developer's absolute paths. Build the same pinned V8 revision and `v8_monolith` for the architecture supported by the target Unity player, then pass those settings to Xcode, for example:

```sh
xcodebuild -project v8_in_unity/xcode_projects/v8_in_unity/v8_in_unity.xcodeproj \
  -target v8_in_unity -configuration Release \
  V8_ROOT="$V8_ROOT" V8_OUT="$V8_ROOT/out.gn/x64.release" build
```

Run the native test target before replacing the checked-in bundle. The automated build/install script covers Windows x64 and Linux x86_64; macOS bundle architecture and Unity integration still require a macOS smoke run.

## ABI and artifact validation

The C++ regression harness checks V8 initialization, evaluation, brain/module updates, callbacks, and the exported wrapper functions. The Unity `V8InUnity.UnitTests` add managed interop coverage, but the native DLL is disabled for the Unity Editor; run those tests in a supported standalone player after replacing a plugin.

The checked-in Windows and macOS binaries remain prebuilt artifacts. A successful native rebuild does not prove that every platform bundle was rebuilt; update and test each target separately. Record the SHA-256 printed by the build script with its toolchain environment when producing a release artifact.

Current checked-in artifact hashes:

- Windows x64 DLL: `89536122a2130754aea0c80a22862126bcd1baef89bf6668f47fa31141e29ced`
- macOS bundle executable: `9280f920c820a04afa5846790869867ed29836509f7dd196a52957303555c4e5`
- Linux x86_64 shared library: `34c9b0c7ccc715aa07e0e667cc6b3358e93e6f9369bc32e5fe9658be8aad44a9`

These hashes identify the current files; they do not prove the build provenance.

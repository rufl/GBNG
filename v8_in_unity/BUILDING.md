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

Validated in Ubuntu 22.04 x86_64 under WSL2: the script builds the pinned V8 monolith, passes all 99 native regression checks, then loads `libv8_in_unity.so` and verifies initialization, `6 * 7 == 42`, and deinitialization before installation. The Unity 6000.6.6f1 Mono player smoke ran on Ubuntu 26.04 under WSL2: it loaded V8, evaluated `6 * 7 == 42`, and exercised `DirectHostDiscovery` without the former `GetIPv6Properties` exception; Linux uses libc `if_nametoindex` as the fallback. Linux standalone builds rewrite AssimpNet's four `libdl.so` imports to `libdl.so.2` and bundle `libminizip.so` beside Assimp, preloading it globally to satisfy `libassimp.so`'s `libminizip.so.1` dependency without `LD_LIBRARY_PATH`. A Linux Mono player on Ubuntu 26.04 under WSL2/WSLg imported the repository's animated Fox FBX through Assimp and ran the asset-preparation step: its Windows-absolute texture references resolved to sibling files, and the model had 1,733 skinned vertices, 19 bones, three animation clips, two diffuse textures, and a generated mesh collider; sampling RunForward changed the rig pose. This does not verify asynchronous `AssetCache.Get` or physical Linux GPU rendering. See `Assets/StreamingAssets/third_party/Minizip-LICENSE.md` for the bundled Ubuntu package version, hash, and Zlib license. The Linux CEF/Monaco path was smoke-tested on Unity 6000.6.6f1 Mono under Ubuntu 26.04 WSL2/WSLg: the page-ready signal arrived, the browser texture was 1280x720, and the editor JS bridge round-tripped code. Cross-host IPv6 multicast remains untested.

## macOS

The checked-in V8 bundle is x86_64-only. The repository now contains a
reproducible universal-bundle build path, but no Apple Silicon artifact is
checked in until it has passed the native and Unity-player smoke tests on that
hardware. The Unity build guard reads the bundle's Mach-O architectures: it
permits x86_64, arm64, or universal players only when the installed bundle
contains the corresponding slice.

Requirements: an Apple Silicon Mac, current Xcode command-line tools,
depot_tools (`fetch`, `gclient`, `gn`, `ninja`), the pinned V8 checkout, and
Rosetta 2. Rosetta is required because a distributable universal bundle builds
and runs the x86_64 regression harness as well as the native arm64 one.

From a directory outside this repository, fetch and sync the exact engine
revision:

```sh
fetch v8
cd v8
git checkout --detach d2e0166ded485df126c765b9196f7edd1ce50f82
gclient sync --nohooks --revision v8@d2e0166ded485df126c765b9196f7edd1ce50f82
```

Then, from the Game Builder repository root, build both architectures, run both
native harnesses, merge their bundle executables with `lipo`, and install the
bundle only after every preceding step succeeds:

```sh
python3 tools/maintenance/build_v8_in_unity_macos.py --v8-root /path/to/v8 \
  --architecture universal
```

The script checks the exact revision and V8 `7.1.0.0` header, creates separate
`out.gn/macos.x64.release` and `out.gn/macos.arm64.release` trees, builds
`v8_monolith`, builds the wrapper and native harness with Xcode, verifies each
thin Mach-O slice, runs each harness under its target CPU (x86_64 through
Rosetta), creates a universal bundle, verifies both slices, and only then
replaces `Assets/Plugins/V8InUnity/v8_in_unity.bundle`. A failure leaves the
installed bundle intact. Record the printed SHA-256 and run a standalone Unity
behavior-code smoke test before committing a new macOS artifact.

`--architecture arm64` validates an Apple-Silicon-only bundle and
`--architecture x64` rebuilds the existing Intel-compatible one. Neither is
appropriate for a public build that must support both Mac CPU families.

The pinned V8 revision predates Apple Silicon. If its GN or Ninja build fails
on current Xcode, do not replace V8 merely to obtain ARM64 support: preserve
the failure log, keep the existing Intel bundle, and validate a separately
reviewed V8 migration against the native and player test matrix.

## ABI and artifact validation

The C++ regression harness checks V8 initialization, evaluation, brain/module updates, callbacks, and the exported wrapper functions. The Unity `V8InUnity.UnitTests` add managed interop coverage, but the native DLL is disabled for the Unity Editor; run those tests in a supported standalone player after replacing a plugin.

The checked-in Windows and macOS binaries remain prebuilt artifacts. A successful native rebuild does not prove that every platform bundle was rebuilt; update and test each target separately. Record the SHA-256 printed by the build script with its toolchain environment when producing a release artifact.

Current checked-in artifact hashes:

- Windows x64 DLL: `89536122a2130754aea0c80a22862126bcd1baef89bf6668f47fa31141e29ced`
- macOS bundle executable: `9280f920c820a04afa5846790869867ed29836509f7dd196a52957303555c4e5`
- Linux x86_64 shared library: `34c9b0c7ccc715aa07e0e667cc6b3358e93e6f9369bc32e5fe9658be8aad44a9`

These hashes identify the current files; they do not prove the build provenance.

#!/usr/bin/env python3
"""Build, test, and install the pinned Windows x64 or Linux x86_64 V8 plugin."""

from __future__ import annotations

import argparse
import hashlib
import os
import platform
import shlex
from pathlib import Path
import shutil
import subprocess
import sys

V8_REVISION = "d2e0166ded485df126c765b9196f7edd1ce50f82"
V8_VERSION = "7.1.0.0"
GN_ARGS = (
    'is_debug=false target_cpu="x64" is_component_build=false '
    "v8_monolithic=true v8_use_external_startup_data=false"
)


def run(
    command: list[str], cwd: Path | None = None, env: dict[str, str] | None = None
) -> None:
    display = subprocess.list2cmdline(command) if os.name == "nt" else shlex.join(command)
    print("$ " + display, flush=True)
    subprocess.run(command, cwd=cwd, env=env, check=True)


def capture(command: list[str], cwd: Path, env: dict[str, str]) -> list[str]:
    print("$ " + shlex.join(command), flush=True)
    result = subprocess.run(
        command, cwd=cwd, env=env, check=True, text=True, capture_output=True
    )
    return result.stdout.splitlines()


def require_tool(name: str) -> str:
    path = shutil.which(name)
    if path is None:
        raise RuntimeError(f"Required build tool not found on PATH: {name}")
    return path


def verify_v8_checkout(v8_root: Path) -> None:
    if not (v8_root / ".git").exists():
        raise RuntimeError(f"Not a V8 git checkout: {v8_root}")

    revision = subprocess.run(
        [require_tool("git"), "rev-parse", "HEAD"],
        cwd=v8_root,
        check=True,
        text=True,
        capture_output=True,
    ).stdout.strip()
    if revision != V8_REVISION:
        raise RuntimeError(
            f"V8 revision mismatch: expected {V8_REVISION}, found {revision}"
        )

    changes = subprocess.run(
        [require_tool("git"), "diff", "--quiet", "HEAD", "--"], cwd=v8_root
    )
    if changes.returncode != 0:
        raise RuntimeError("V8 source has tracked local changes; refusing to build")

    version_header = v8_root / "include" / "v8-version.h"
    if not version_header.is_file():
        raise RuntimeError(f"Missing V8 version header: {version_header}")
    version_text = version_header.read_text(encoding="utf-8")
    expected_macros = {
        "V8_MAJOR_VERSION": "7",
        "V8_MINOR_VERSION": "1",
        "V8_BUILD_NUMBER": "0",
        "V8_PATCH_LEVEL": "0",
    }
    for macro, expected in expected_macros.items():
        if f"#define {macro} {expected}" not in version_text:
            raise RuntimeError(f"Unexpected {macro} in {version_header}")


def install_plugin(repo_root: Path, artifact: Path, filename: str) -> None:
    installed = repo_root / "Assets" / "Plugins" / "V8InUnity" / filename
    metadata = installed.with_name(installed.name + ".meta")
    if not metadata.is_file():
        raise RuntimeError(f"Missing Unity plugin importer metadata: {metadata}")
    shutil.copy2(artifact, installed)
    digest = hashlib.sha256(installed.read_bytes()).hexdigest()
    print(f"Installed {installed}")
    print(f"SHA-256: {digest}")
    print(f"Verified V8 {V8_VERSION} at {V8_REVISION}")


def build_windows(repo_root: Path, v8_root: Path, v8_out: Path) -> None:
    gn = require_tool("gn")
    ninja = require_tool("ninja")
    msbuild = require_tool("msbuild")

    run([gn, "gen", str(v8_out), f"--args={GN_ARGS}"])
    run([ninja, "-C", str(v8_out), "v8_monolith"])
    monolith = v8_out / "obj" / "v8_monolith.lib"
    if not monolith.is_file():
        raise RuntimeError(f"V8 build did not produce {monolith}")

    solution = repo_root / "v8_in_unity" / "v8_in_unity.sln"
    run(
        [
            msbuild,
            str(solution),
            "/m",
            "/p:Configuration=Release",
            "/p:Platform=x64",
            f"/p:V8Root={v8_root}",
            f"/p:V8Out={v8_out}",
        ]
    )

    output_dir = solution.parent / "x64" / "Release"
    test_exe = output_dir / "v8_in_unity_test.exe"
    plugin_dll = output_dir / "v8_in_unity.dll"
    if not test_exe.is_file() or not plugin_dll.is_file():
        raise RuntimeError(
            f"Expected native outputs in {output_dir}; build did not produce both files"
        )

    run([str(test_exe)], cwd=output_dir)
    install_plugin(repo_root, plugin_dll, plugin_dll.name)


def build_linux(repo_root: Path, v8_root: Path, v8_out: Path) -> None:
    if platform.machine().lower() not in {"x86_64", "amd64"}:
        raise RuntimeError("The Linux V8 plugin build supports x86_64 only")

    python2 = shutil.which("python2.7") or shutil.which("python2")
    if python2 is None:
        raise RuntimeError("Python 2.7 is required by the pinned V8 build scripts")

    python_shim_dir = v8_out / "maintenance-bin"
    python_shim_dir.mkdir(parents=True, exist_ok=True)
    python_shim = python_shim_dir / "python"
    python2_path = Path(python2).resolve()
    if python_shim.is_symlink():
        if python_shim.resolve() != python2_path:
            raise RuntimeError(f"Unexpected Python shim already exists: {python_shim}")
    elif python_shim.exists():
        raise RuntimeError(f"Refusing to replace existing file: {python_shim}")
    else:
        python_shim.symlink_to(python2_path)

    env = os.environ.copy()
    env["PATH"] = str(python_shim_dir) + os.pathsep + env.get("PATH", "")
    gn = v8_root / "buildtools" / "linux64" / "gn"
    if not gn.is_file():
        gn = Path(require_tool("gn"))
    ninja = require_tool("ninja")
    clang = (
        v8_root
        / "third_party"
        / "llvm-build"
        / "Release+Asserts"
        / "bin"
        / "clang++"
    )
    if not clang.is_file():
        raise RuntimeError(
            f"Missing V8-pinned Clang: {clang}; run tools/clang/scripts/update.py"
        )

    sysroot = v8_root / "build" / "linux" / "debian_sid_amd64-sysroot"
    if not sysroot.is_dir():
        raise RuntimeError(
            f"Missing V8 Linux sysroot: {sysroot}; run install-sysroot.py --arch=x64"
        )

    linux_gn_args = f"{GN_ARGS} is_clang=true"
    run(
        [str(gn), "gen", str(v8_out), f"--args={linux_gn_args}"],
        cwd=v8_root,
        env=env,
    )
    run([ninja, "-C", str(v8_out), "v8_monolith"], cwd=v8_root, env=env)

    monolith = v8_out / "obj" / "libv8_monolith.a"
    if not monolith.is_file():
        raise RuntimeError(f"V8 build did not produce {monolith}")

    target = "//:v8_monolith"

    def gn_values(attribute: str) -> list[str]:
        return capture(
            [str(gn), "desc", str(v8_out.relative_to(v8_root)), target, attribute],
            cwd=v8_root,
            env=env,
        )

    defines = [f"-D{value}" for value in gn_values("defines")]
    include_dirs = []
    for value in gn_values("include_dirs"):
        if not value.startswith("//"):
            raise RuntimeError(f"Unexpected V8 include path from GN: {value}")
        include_dirs.append(f"-I{v8_root / value[2:]}")

    # V8's GN warning policy is stricter than the existing wrapper projects.
    # Keep its warnings enabled, but don't turn legacy wrapper warnings into errors.
    compile_flags = (
        gn_values("cflags")
        + gn_values("cflags_cc")
        + defines
        + include_dirs
        + ["-Wno-error"]
    )
    link_flags = gn_values("ldflags")
    libraries = [f"-l{library}" for library in gn_values("libs")]
    # V8's monolith omits libc++ source_set objects; d8 links those separately.
    runtime_object_dirs = (
        v8_out / "obj" / "buildtools" / "third_party" / "libc++" / "libc++",
        v8_out / "obj" / "buildtools" / "third_party" / "libc++abi" / "libc++abi",
    )
    runtime_objects = sorted(
        object_file
        for directory in runtime_object_dirs
        for object_file in directory.glob("*.o")
    )
    if not runtime_objects:
        raise RuntimeError("V8 build did not produce its libc++ runtime objects")


    output_dir = v8_out / "obj" / "v8_in_unity"
    output_dir.mkdir(parents=True, exist_ok=True)
    wrapper_object = output_dir / "v8_in_unity.o"
    test_object = output_dir / "v8_in_unity_test.o"
    plugin = output_dir / "libv8_in_unity.so"
    test_exe = output_dir / "v8_in_unity_test"
    wrapper_source = repo_root / "v8_in_unity" / "v8_in_unity" / "v8_in_unity.cpp"
    test_source = (
        repo_root / "v8_in_unity" / "v8_in_unity_test" / "v8_in_unity_test.cpp"
    )

    run(
        [
            str(clang),
            *compile_flags,
            "-c",
            str(wrapper_source),
            "-o",
            str(wrapper_object),
        ],
        cwd=v8_out,
        env=env,
    )
    run(
        [
            str(clang),
            *compile_flags,
            "-c",
            str(test_source),
            "-o",
            str(test_object),
        ],
        cwd=v8_out,
        env=env,
    )

    def link(output: Path, objects: list[Path], shared: bool) -> None:
        run(
            [
                str(clang),
                *link_flags,
                *(["-shared", f"-Wl,-soname,{output.name}"] if shared else []),
                "-o",
                str(output),
                *(str(obj) for obj in objects),
                "-Wl,--start-group",
                str(monolith),
                *(str(runtime_object) for runtime_object in runtime_objects),
                *libraries,
                "-Wl,--end-group",
                "-pthread",
            ],
            cwd=v8_out,
            env=env,
        )

    link(plugin, [wrapper_object], shared=True)
    link(test_exe, [wrapper_object, test_object], shared=False)
    run([str(test_exe)], cwd=output_dir, env=env)
    shared_library_smoke = (
        "import ctypes, sys\n"
        "plugin = ctypes.CDLL(sys.argv[1])\n"
        "plugin.InitializeV8.argtypes = [ctypes.c_char_p]\n"
        "plugin.InitializeV8.restype = ctypes.c_int\n"
        "plugin.EvaluateToInteger.argtypes = [ctypes.c_char_p]\n"
        "plugin.EvaluateToInteger.restype = ctypes.c_int\n"
        "if plugin.InitializeV8(sys.argv[2].encode()) != 0: raise SystemExit('InitializeV8 failed')\n"
        "if plugin.EvaluateToInteger(b'6 * 7') != 42: raise SystemExit('EvaluateToInteger failed')\n"
        "if plugin.DeinitializeV8() != 0: raise SystemExit('DeinitializeV8 failed')\n"
        "print('Shared library smoke passed')\n"
    )
    run(
        [sys.executable, "-c", shared_library_smoke, str(plugin), str(output_dir)],
        cwd=output_dir,
        env=env,
    )
    install_plugin(repo_root, plugin, plugin.name)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--v8-root",
        default=os.environ.get("V8_ROOT"),
        help="V8 checkout at the pinned revision (defaults to V8_ROOT)",
    )
    args = parser.parse_args()

    if not args.v8_root:
        parser.error("Pass --v8-root or set V8_ROOT")

    repo_root = Path(__file__).resolve().parents[2]
    v8_root = Path(args.v8_root).resolve()
    v8_out = v8_root / "out.gn" / "x64.release"
    verify_v8_checkout(v8_root)

    if os.name == "nt":
        build_windows(repo_root, v8_root, v8_out)
    elif sys.platform.startswith("linux"):
        build_linux(repo_root, v8_root, v8_out)
    else:
        parser.error("This build/install script targets Windows x64 and Linux x86_64")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, RuntimeError, subprocess.CalledProcessError) as error:
        print(f"V8 build failed: {error}", file=sys.stderr)
        raise SystemExit(1)

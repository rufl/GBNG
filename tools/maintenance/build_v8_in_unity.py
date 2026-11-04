#!/usr/bin/env python3
"""Build, test, and install the pinned Windows x64 V8 Unity plugin."""

from __future__ import annotations

import argparse
import hashlib
import os
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


def run(command: list[str], cwd: Path | None = None) -> None:
    print("$ " + subprocess.list2cmdline(command), flush=True)
    subprocess.run(command, cwd=cwd, check=True)


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


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--v8-root",
        default=os.environ.get("V8_ROOT"),
        help="V8 checkout at the pinned revision (defaults to V8_ROOT)",
    )
    args = parser.parse_args()

    if os.name != "nt":
        parser.error("This build/install script targets Windows x64 only")
    if not args.v8_root:
        parser.error("Pass --v8-root or set V8_ROOT")

    repo_root = Path(__file__).resolve().parents[2]
    v8_root = Path(args.v8_root).resolve()
    v8_out = v8_root / "out.gn" / "x64.release"
    verify_v8_checkout(v8_root)

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

    installed_dll = repo_root / "Assets" / "Plugins" / "V8InUnity" / "v8_in_unity.dll"
    shutil.copy2(plugin_dll, installed_dll)
    digest = hashlib.sha256(installed_dll.read_bytes()).hexdigest()
    print(f"Installed {installed_dll}")
    print(f"SHA-256: {digest}")
    print(f"Verified V8 {V8_VERSION} at {V8_REVISION}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, RuntimeError, subprocess.CalledProcessError) as error:
        print(f"V8 build failed: {error}", file=sys.stderr)
        raise SystemExit(1)

#!/usr/bin/env python3
"""Build, test, and install a pinned macOS V8 Unity plugin.

Run this only on macOS. Apple Silicon is required for an arm64 or universal
bundle; x64 builds on Apple Silicon run through Rosetta 2 and are tested there.
"""

from __future__ import annotations

import argparse
import hashlib
import os
from pathlib import Path
import platform
import shutil
import subprocess
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
from build_v8_in_unity import V8_REVISION, V8_VERSION, require_tool, run, verify_v8_checkout


def remove_path(path: Path) -> None:
    if path.is_dir() and not path.is_symlink():
        shutil.rmtree(path)
    elif path.exists() or path.is_symlink():
        path.unlink()


def macos_arch_name(architecture: str) -> str:
    return "x86_64" if architecture == "x64" else architecture


def gn_args(architecture: str) -> str:
    return (
        "is_debug=false target_os=\"mac\" "
        f'target_cpu="{architecture}" '
        "is_component_build=false v8_monolithic=true "
        "v8_use_external_startup_data=false"
    )


def architecture_command(architecture: str, command: list[str], host: str) -> list[str]:
    if architecture == "x64" and host == "arm64":
        return ["/usr/bin/arch", "-x86_64", *command]
    return command


def run_for_architecture(
    architecture: str,
    command: list[str],
    host: str,
    cwd: Path | None = None,
    env: dict[str, str] | None = None,
) -> None:
    run(architecture_command(architecture, command, host), cwd=cwd, env=env)


def bundle_executable(bundle: Path) -> Path:
    executable = bundle / "Contents" / "MacOS" / "v8_in_unity"
    if not executable.is_file():
        raise RuntimeError(f"Xcode did not produce the bundle executable: {executable}")
    return executable


def verify_architecture(lipo: str, executable: Path, architecture: str) -> None:
    run([lipo, "-verify_arch", macos_arch_name(architecture), str(executable)])


def xcode_build(
    architecture: str,
    xcodebuild: str,
    project: Path,
    target: str,
    output_dir: Path,
    v8_root: Path,
    v8_out: Path,
) -> None:
    output_dir.mkdir(parents=True, exist_ok=True)
    run(
        [
            xcodebuild,
            "-project",
            str(project),
            "-target",
            target,
            "-configuration",
            "Release",
            f"CONFIGURATION_BUILD_DIR={output_dir}",
            f"V8_ROOT={v8_root}",
            f"V8_OUT={v8_out}",
            f"ARCHS={macos_arch_name(architecture)}",
            f"MACOSX_DEPLOYMENT_TARGET={'11.0' if architecture == 'arm64' else '10.13'}",
            "ONLY_ACTIVE_ARCH=NO",
            "CODE_SIGNING_ALLOWED=NO",
            "build",
        ]
    )


def build_architecture(
    repo_root: Path,
    v8_root: Path,
    architecture: str,
    host: str,
    gn: str,
    ninja: str,
    xcodebuild: str,
    lipo: str,
) -> Path:
    v8_out = v8_root / "out.gn" / f"macos.{architecture}.release"
    run(
        [gn, "gen", str(v8_out), f"--args={gn_args(architecture)}"],
        cwd=v8_root,
    )
    run(
        [ninja, "-C", str(v8_out), "v8_monolith"],
        cwd=v8_root,
    )
    monolith = v8_out / "obj" / "libv8_monolith.a"
    if not monolith.is_file():
        raise RuntimeError(f"V8 build did not produce {monolith}")

    build_root = v8_out / "v8_in_unity"
    plugin_output = build_root / "plugin"
    test_output = build_root / "test"
    xcode_build(
        architecture,
        xcodebuild,
        repo_root / "v8_in_unity" / "xcode_projects" / "v8_in_unity" / "v8_in_unity.xcodeproj",
        "v8_in_unity",
        plugin_output,
        v8_root,
        v8_out,
    )
    xcode_build(
        architecture,
        xcodebuild,
        repo_root / "v8_in_unity" / "xcode_projects" / "v8_in_unity_test" / "v8_in_unity_test.xcodeproj",
        "v8_in_unity_test",
        test_output,
        v8_root,
        v8_out,
    )

    bundle = plugin_output / "v8_in_unity.bundle"
    executable = bundle_executable(bundle)
    verify_architecture(lipo, executable, architecture)
    test_executable = test_output / "v8_in_unity_test"
    if not test_executable.is_file():
        raise RuntimeError(f"Xcode did not produce the native test executable: {test_executable}")
    run_for_architecture(architecture, [str(test_executable)], host, cwd=test_output)
    return bundle


def create_universal_bundle(lipo: str, x64_bundle: Path, arm64_bundle: Path, output: Path) -> Path:
    if output.exists() or output.is_symlink():
        remove_path(output)
    shutil.copytree(arm64_bundle, output)
    executable = bundle_executable(output)
    run(
        [
            lipo,
            "-create",
            str(bundle_executable(x64_bundle)),
            str(bundle_executable(arm64_bundle)),
            "-output",
            str(executable),
        ]
    )
    run([lipo, "-verify_arch", "x86_64", "arm64", str(executable)])
    return output


def install_bundle(repo_root: Path, bundle: Path) -> None:
    installed = repo_root / "Assets" / "Plugins" / "V8InUnity" / "v8_in_unity.bundle"
    metadata = installed.with_name(installed.name + ".meta")
    if not metadata.is_file():
        raise RuntimeError(f"Missing Unity plugin importer metadata: {metadata}")

    staging = installed.with_name(installed.name + ".installing")
    backup = installed.with_name(installed.name + ".previous")
    if staging.exists() or staging.is_symlink() or backup.exists() or backup.is_symlink():
        raise RuntimeError(f"Refusing to overwrite a stale install staging path beside {installed}")

    shutil.copytree(bundle, staging)
    try:
        if installed.exists() or installed.is_symlink():
            installed.replace(backup)
        staging.replace(installed)
    except BaseException:
        if not installed.exists() and (backup.exists() or backup.is_symlink()):
            backup.replace(installed)
        raise
    else:
        if backup.exists() or backup.is_symlink():
            remove_path(backup)

    digest = hashlib.sha256(bundle_executable(installed).read_bytes()).hexdigest()
    print(f"Installed {installed}")
    print(f"SHA-256: {digest}")
    print(f"Verified V8 {V8_VERSION} at {V8_REVISION}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--v8-root",
        default=os.environ.get("V8_ROOT"),
        help="V8 checkout at the pinned revision (defaults to V8_ROOT)",
    )
    parser.add_argument(
        "--architecture",
        choices=("x64", "arm64", "universal"),
        default="universal",
        help="macOS plugin architecture to install (default: universal)",
    )
    args = parser.parse_args()
    if not args.v8_root:
        parser.error("Pass --v8-root or set V8_ROOT")
    if platform.system() != "Darwin":
        parser.error("This build/install script runs on macOS only")

    host = platform.machine().lower()
    if host == "aarch64":
        host = "arm64"
    if host not in {"x86_64", "arm64"}:
        parser.error(f"Unsupported macOS host architecture: {host}")
    if args.architecture in {"arm64", "universal"} and host != "arm64":
        parser.error("An Apple Silicon host is required to build and run the arm64 V8 native harness")

    repo_root = Path(__file__).resolve().parents[2]
    v8_root = Path(args.v8_root).resolve()
    verify_v8_checkout(v8_root)
    gn = require_tool("gn")
    ninja = require_tool("ninja")
    xcodebuild = require_tool("xcodebuild")
    lipo = require_tool("lipo")

    architectures = ("x64", "arm64") if args.architecture == "universal" else (args.architecture,)
    bundles = {
        architecture: build_architecture(
            repo_root, v8_root, architecture, host, gn, ninja, xcodebuild, lipo
        )
        for architecture in architectures
    }
    if args.architecture == "universal":
        bundle = create_universal_bundle(
            lipo,
            bundles["x64"],
            bundles["arm64"],
            v8_root / "out.gn" / "macos.universal.release" / "v8_in_unity.bundle",
        )
    else:
        bundle = bundles[args.architecture]
    install_bundle(repo_root, bundle)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, RuntimeError, subprocess.CalledProcessError) as error:
        print(f"V8 macOS build failed: {error}", file=sys.stderr)
        raise SystemExit(1)

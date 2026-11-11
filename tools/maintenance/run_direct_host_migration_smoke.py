#!/usr/bin/env python3
"""Exercise direct-host recovery with three standalone Game Builder players.

The original host is terminated at the operating-system level after two clients
receive their migration tickets. This deliberately avoids graceful directory
unregistration, which is the failure mode host migration must recover from.
"""

import argparse
import os
from pathlib import Path
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import urllib.request


MARKER = "DIRECT_HOST_MIGRATION_PASS"
ROOT = Path(__file__).resolve().parents[2]
DIRECTORY_SERVER = ROOT / "tools" / "public-session-directory" / "server.py"


def free_port(sock_type):
    with socket.socket(socket.AF_INET, sock_type) as listener:
        listener.bind(("127.0.0.1", 0))
        return listener.getsockname()[1]


def wait_for(predicate, timeout, message, processes):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if predicate():
            return
        for name, process in processes.items():
            if process is not None and process.poll() is not None:
                raise RuntimeError("{} exited before {} (code {}).".format(name, message, process.returncode))
        time.sleep(0.1)
    raise RuntimeError("Timed out waiting for {}.".format(message))


def terminate(process):
    if process is not None and process.poll() is None:
        process.kill()
        process.wait(timeout=15)


def read_log(path):
    try:
        return path.read_text(encoding="utf-8", errors="replace")
    except OSError:
        return "<log unavailable>"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--executable", required=True, type=Path,
                        help="Path to a no-Steam Windows Game Builder player build.")
    parser.add_argument("--join-address", default="127.0.0.1",
                        help="Host address exercised by the candidate and follower.")
    parser.add_argument("--relay", action="store_true",
                        help="Route player connections through the local UDP relay.")
    parser.add_argument("--relay-address", default="127.0.0.1",
                        help="IPv4 address advertised by the local UDP relay.")
    parser.add_argument("--timeout", type=float, default=360,
                        help="Maximum seconds for the candidate and follower to finish.")
    parser.add_argument("--keep-artifacts", action="store_true",
                        help="Keep the temporary coordination files and player logs.")
    args = parser.parse_args()

    executable = args.executable.resolve()
    if not executable.is_file():
        parser.error("Player executable does not exist: {}".format(executable))
    if not DIRECTORY_SERVER.is_file():
        parser.error("Directory server does not exist: {}".format(DIRECTORY_SERVER))
    if args.relay_address != "127.0.0.1" and not args.relay:
        parser.error("--relay-address requires --relay")

    work_dir = Path(tempfile.mkdtemp(prefix="GameBuilderDirectMigration-"))
    coordination = work_dir / "coordination.txt"
    logs = {role: work_dir / (role + ".log") for role in ("host", "candidate", "follower")}
    directory_port = free_port(socket.SOCK_STREAM)
    game_port = free_port(socket.SOCK_DGRAM)
    relay_port = free_port(socket.SOCK_DGRAM) if args.relay else None
    relay_bind = "127.0.0.1" if args.relay_address == "127.0.0.1" else "0.0.0.0"
    environment = os.environ.copy()
    environment["GAMEBUILDER_SESSION_DIRECTORY"] = "http://127.0.0.1:{}".format(directory_port)
    directory = None
    players = {"host": None, "candidate": None, "follower": None}
    log_handles = {}

    try:
        directory_command = [
            sys.executable, str(DIRECTORY_SERVER),
            "--bind", "127.0.0.1", "--port", str(directory_port)
        ]
        if args.relay:
            directory_command.extend([
                "--relay-bind", relay_bind, "--relay-control-port", str(relay_port),
                "--relay-public-address", args.relay_address,
                "--relay-data-port-start", "0", "--relay-data-port-end", "0"
            ])
        else:
            directory_command.append("--relay-disabled")
        directory = subprocess.Popen(
            directory_command,
            cwd=str(ROOT), stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        health_url = "http://127.0.0.1:{}/health".format(directory_port)
        wait_for(lambda: _directory_healthy(health_url), 30, "the local session directory", {"directory": directory})

        player_work_dirs = {}
        for role in ("host", "candidate", "follower"):
            player_work_dirs[role] = work_dir / role
            player_work_dirs[role].mkdir()
            log_handles[role] = logs[role].open("w", encoding="utf-8")

        players["host"] = _start_player(
            executable, "host", coordination, game_port, args.join_address, args.relay, environment,
            log_handles["host"], player_work_dirs["host"])
        wait_for(coordination.exists, 120, "the initial host registration", players)
        players["candidate"] = _start_player(
            executable, "candidate", coordination, game_port, args.join_address, args.relay, environment,
            log_handles["candidate"], player_work_dirs["candidate"])
        wait_for(lambda: (work_dir / "coordination.txt.candidate-ready").exists(),
                 120, "the candidate initial connection", players)
        players["follower"] = _start_player(
            executable, "follower", coordination, game_port, args.join_address, args.relay, environment,
            log_handles["follower"], player_work_dirs["follower"])
        host_kill_ready = work_dir / "coordination.txt.host-kill-ready"
        wait_for(host_kill_ready.exists, 120, "both clients before host termination", players)

        # Abrupt process termination preserves the directory entry and forces migration.
        terminate(players["host"])
        players["host"] = None

        follower_pass = work_dir / "coordination.txt.follower-pass"
        candidate_pass = work_dir / "coordination.txt.candidate-migrated"
        candidate_fail = work_dir / "coordination.txt.candidate-fail"
        follower_fail = work_dir / "coordination.txt.follower-fail"
        wait_for(lambda: candidate_fail.exists() or follower_fail.exists()
                 or follower_pass.exists() and candidate_pass.exists(), args.timeout,
                 "the migrated host and follower recovery", players)
        if candidate_fail.exists():
            raise RuntimeError("candidate reported: " + candidate_fail.read_text(encoding="utf-8"))
        if follower_fail.exists():
            raise RuntimeError("follower reported: " + follower_fail.read_text(encoding="utf-8"))
        for role in ("candidate", "follower"):
            players[role].wait(timeout=30)
            if players[role].returncode != 0:
                raise RuntimeError("{} exited with code {}.".format(role, players[role].returncode))
            if MARKER not in read_log(logs[role]):
                raise RuntimeError("{} did not emit {}.".format(role, MARKER))

        transport = "relay" if args.relay else "direct"
        endpoint = args.relay_address if args.relay else args.join_address
        print("DIRECT_HOST_MIGRATION_SMOKE_PASS|transport={}|address={}|port={}|generation={}".format(
            transport, endpoint, game_port, candidate_pass.read_text(encoding="utf-8").strip()))
    except Exception as error:
        print("DIRECT_HOST_MIGRATION_SMOKE_FAIL|{}".format(error), file=sys.stderr)
        for role, path in logs.items():
            print("\n--- {} log ---\n{}".format(role, read_log(path)[-12000:]), file=sys.stderr)
        raise SystemExit(1)
    finally:
        for process in players.values():
            terminate(process)
        for handle in log_handles.values():
            handle.close()
        terminate(directory)
        if args.keep_artifacts:
            print("DIRECT_HOST_MIGRATION_SMOKE_ARTIFACTS|{}".format(work_dir))
        else:
            shutil.rmtree(work_dir, ignore_errors=True)


def _directory_healthy(url):
    try:
        with urllib.request.urlopen(url, timeout=1) as response:
            return response.status == 200
    except OSError:
        return False


def _start_player(executable, role, coordination, port, join_address, use_relay, environment, log, working_directory):
    command = [
        str(executable), "-batchmode", "-nographics",
        "--direct-host-migration-smoke=" + role,
        "--direct-host-migration-smoke-coordination=" + str(coordination),
        "--direct-host-migration-smoke-port=" + str(port),
        "--direct-host-migration-smoke-address=" + join_address
    ]
    if use_relay:
        command.append("--direct-host-migration-smoke-relay")
    return subprocess.Popen(
        command, cwd=str(working_directory), env=environment, stdout=log, stderr=subprocess.STDOUT)


if __name__ == "__main__":
    main()

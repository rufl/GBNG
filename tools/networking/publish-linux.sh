#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/../.." && pwd)"
project="$repo_root/services/GBNG.Matchmaking/GBNG.Matchmaking.csproj"

if [[ ! -f "$project" ]]; then
    printf 'Matchmaking project not found: %s\n' "$project" >&2
    exit 1
fi
if ! command -v dotnet >/dev/null 2>&1; then
    printf 'The .NET SDK is required. Install .NET 10 SDK and ensure dotnet is on PATH.\n' >&2
    exit 1
fi
if ! dotnet --list-sdks | grep -Eq '(^|[[:space:]])10\.'; then
    printf 'A .NET 10 SDK is required to publish this project. Install it and ensure dotnet is on PATH.\n' >&2
    exit 1
fi

publish_dir="${1:-$script_dir/publish/linux-x64}"
if [[ "$publish_dir" != /* ]]; then
    publish_dir="$repo_root/$publish_dir"
fi
mkdir -p -- "$publish_dir"
dotnet publish "$project" --configuration Release --runtime linux-x64 --self-contained true -p:UseAppHost=true --output "$publish_dir"
printf 'Published self-contained linux-x64 matchmaking service to: %s\n' "$publish_dir"
printf 'This command only publishes files; it does not install or start a service.\n'

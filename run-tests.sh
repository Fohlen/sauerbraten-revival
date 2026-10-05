#!/usr/bin/env bash
# Run the Sauerbraten Revival unit tests (GoDotTest) headless.
#
# The tests live in test/ and are compiled into the game assembly in Debug
# builds, so they run inside the Godot runtime rather than via `dotnet test`.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
GODOT="${GODOT:-/Applications/Godot_mono.app/Contents/MacOS/Godot}"

if [ ! -x "$GODOT" ]; then
    echo "ERROR: Godot .NET editor not found at '$GODOT' (set GODOT=/path/to/Godot)"
    exit 1
fi

cd "$HERE"
dotnet build SauerbratenRevival.csproj -c Debug --nologo
exec "$GODOT" --headless --path . --run-tests --quit-on-finish

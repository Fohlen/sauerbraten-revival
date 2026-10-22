#!/usr/bin/env bash
# Profile the GoDotTest suite with dotnet-trace to find what makes it slow.
#
# The tests run inside the Godot runtime (see run-tests.sh), so dotnet-trace
# attaches to the Godot process rather than launching `dotnet test`. Two quirks
# drive the design:
#
#   * `--duration` is the cleanest way to stop a collection, but if the traced
#     process exits first the .nettrace is truncated and `dotnet-trace report`
#     fails with "Read past end of stream". So this runs Godot WITHOUT
#     --quit-on-finish (it lingers after the tests) and stops the collector
#     itself with SIGTERM once the log reports "Finished testing".
#   * With a Homebrew / dotnet-install layout the dotnet-trace apphost cannot
#     find the runtime, so DOTNET_ROOT is exported for it when unset.
#
# Artifacts land in <output> (default ${TMPDIR:-/tmp}/sauerbraten-tests):
#   tests.log              full GoDotTest output (per-suite pass/fail)
#   tests.nettrace         the raw trace (open in PerfView)
#   tests.speedscope.json  flamegraph; open at https://speedscope.app and pick
#                          the thread running the tests (idle pool threads
#                          otherwise dominate the report)
#
# `dotnet-trace report ... topN` aggregates every thread, so its "top" is
# usually WaitHandle/LowLevelLifoSemaphore waits from idle pool threads — use
# the speedscope view and filter to the test thread for real hot spots.
#
# Usage:
#   tools/trace-tests.sh [options]
#
# Options:
#   -o, --output DIR    output directory (default ${TMPDIR:-/tmp}/sauerbraten-tests)
#   -s, --suite NAME    trace one suite or Suite.Method (GoDotTest --run-tests=NAME)
#   -w, --wait SECONDS  give up waiting for the suite after SECONDS (default 600)
#   -n, --no-build      skip `dotnet build` (reuse the current Debug assembly)
#   -h, --help          show this help
#
# Environment:
#   GODOT        Godot .NET editor binary (default below)
#   DOTNET_ROOT  .NET root for the dotnet-trace apphost (auto-detected if unset)
set -euo pipefail

HERE="$(cd "$(dirname "$0")/.." && pwd)"
GODOT="${GODOT:-/Applications/Godot_mono.app/Contents/MacOS/Godot}"
OUT="${TMPDIR:-/tmp}/sauerbraten-tests"
SUITE=""
WAIT=600
BUILD=1

usage() { sed -n '2,/^set -euo/p' "$0" | sed '$d; s/^# \{0,1\}//'; exit "${1:-0}"; }

while [ $# -gt 0 ]; do
    case "$1" in
        -o|--output)   OUT="$2"; shift 2 ;;
        -s|--suite)    SUITE="$2"; shift 2 ;;
        -w|--wait)     WAIT="$2"; shift 2 ;;
        -n|--no-build) BUILD=0; shift ;;
        -h|--help)     usage 0 ;;
        *)             echo "ERROR: unknown option '$1'" >&2; usage 1 ;;
    esac
done

[ -x "$GODOT" ] || { echo "ERROR: Godot .NET editor not found at '$GODOT' (set GODOT=/path/to/Godot)" >&2; exit 1; }

TRACE="$(command -v dotnet-trace || true)"
[ -n "$TRACE" ] || TRACE="$HOME/.dotnet/tools/dotnet-trace"
[ -x "$TRACE" ] || { echo "ERROR: dotnet-trace not found; install with 'dotnet tool install --global dotnet-trace'" >&2; exit 1; }

if [ -z "${DOTNET_ROOT:-}" ]; then
    DOTNET_ROOT="$(dotnet --list-sdks 2>/dev/null | head -n1 | sed -E 's/.*\[(.*)\]/\1/; s#/sdk$##')"
    [ -n "$DOTNET_ROOT" ] && export DOTNET_ROOT
fi

mkdir -p "$OUT"
LOG="$OUT/tests.log"
NETTRACE="$OUT/tests.nettrace"
SPEEDSCOPE_BASE="$OUT/tests"   # dotnet-trace appends .speedscope.json
rm -f "$LOG" "$NETTRACE" "$SPEEDSCOPE_BASE.speedscope.json"

cd "$HERE"
if [ "$BUILD" -eq 1 ]; then
    echo "== Building (Debug) =="
    dotnet build SauerbratenRevival.csproj -c Debug --nologo
fi

ARGS=(--headless --path . --run-tests)
[ -n "$SUITE" ] && ARGS=(--headless --path . "--run-tests=$SUITE")

cleanup() {
    [ -n "${TPID:-}" ] && kill "$TPID" 2>/dev/null || true
    [ -n "${GPID:-}" ] && kill "$GPID" 2>/dev/null || true
}
trap cleanup EXIT

echo "== Running tests under dotnet-trace =="
# No --quit-on-finish: the process must outlive the collector so the trace can
# be closed cleanly (an early exit truncates the .nettrace).
"$GODOT" "${ARGS[@]}" > "$LOG" 2>&1 &
GPID=$!

sleep 1   # let the embedded .NET runtime come up before attaching
"$TRACE" collect -p "$GPID" --profile dotnet-sampled-thread-time -o "$NETTRACE" > "$OUT/trace.log" 2>&1 &
TPID=$!

deadline=$(( SECONDS + WAIT ))
while ! grep -q "Finished testing" "$LOG" 2>/dev/null; do
    if ! kill -0 "$GPID" 2>/dev/null; then break; fi
    if [ "$SECONDS" -ge "$deadline" ]; then
        echo "WARNING: still running after ${WAIT}s; stopping the trace anyway" >&2
        break
    fi
    sleep 0.5
done

# SIGTERM makes dotnet-trace stop the session and flush a valid .nettrace.
if kill -0 "$TPID" 2>/dev/null; then
    kill -TERM "$TPID"
    wait "$TPID" 2>/dev/null || true
fi
TPID=""
kill "$GPID" 2>/dev/null || true
wait "$GPID" 2>/dev/null || true
GPID=""

echo
echo "== Top functions across all threads (idle pool waits dominate; see speedscope) =="
"$TRACE" report "$NETTRACE" topN -n 15 || true

echo
echo "== Converting to speedscope =="
"$TRACE" convert --format speedscope "$NETTRACE" -o "$SPEEDSCOPE_BASE" || true

echo
echo "artifacts:"
echo "  $LOG   (test results)"
echo "  $NETTRACE"
echo "  $SPEEDSCOPE_BASE.speedscope.json  (open at https://speedscope.app)"

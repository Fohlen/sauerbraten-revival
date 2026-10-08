#!/usr/bin/env bash
# Capture a fresh A2 movement trace from the instrumented original client.
#
# Clean workflow (docs/movement-verification.md):
#   1. tools/refharness/build_instrumented.sh   # pristine src + patches -> client
#   2. tools/refharness/capture.sh [map]
#   3. play (walk, strafe, jump, ramps, stairs, slopes); quit normally
#   4. the trace lands in traces/ and can be replayed by test/A3ReplayTest.cs
#
# Environment:
#   SAUER_REF   reference dump root (default: ~/Downloads/sauerbraten-code-r6889)
#   SAUER_HOME  client home dir     (default: ~/.sauerbraten — tuned: maxfps 200,
#               sensitivity 3, fov 100)
#   TRACE       output base path    (default: <repo>/tools/refharness/traces/<name>.csv)
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
REF="${SAUER_REF:-$HOME/Downloads/sauerbraten-code-r6889}"
HOME_DIR="${SAUER_HOME:-$HOME/.sauerbraten}"
MAP="${1:-base/church51}"
NAME="${CAPTURE_NAME:-session-clean-$(date +%Y%m%d-%H%M%S)}"
TRACE="${TRACE:-$HERE/traces/$NAME.csv}"
CLIENT="$HERE/instrumented/build/sauer_client"

[ -x "$CLIENT" ] || { echo "ERROR: instrumented client missing; run tools/refharness/build_instrumented.sh"; exit 1; }
[ -d "$REF" ]    || { echo "ERROR: reference dump not found at '$REF' (set SAUER_REF)"; exit 1; }

mkdir -p "$(dirname "$TRACE")"
echo "reference: $REF"
echo "home:      $HOME_DIR"
echo "map:       $MAP"
echo "trace:     $TRACE"
echo
echo "Play the map. The trace + .events are written live; quit the client (Esc) to finish."
echo

cd "$REF"
SAUER_TRACE="$TRACE" SAUER_INPUTTRACE="$TRACE.input.csv" \
  "$CLIENT" -q"$HOME_DIR" -l"$MAP"

echo
echo "captured:"
ls -la "$TRACE" "$TRACE.events" "$TRACE.input.csv" 2>/dev/null || true
echo "clips:   ${TRACE}.clips (if deformed cubes were touched)"

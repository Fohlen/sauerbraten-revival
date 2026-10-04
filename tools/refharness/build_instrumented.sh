#!/usr/bin/env bash
# Build an instrumented Sauerbraten client for A2 movement capture, from a
# read-only copy of the reference dump. The dump is NEVER modified: this script
# copies its src/ into an ignored build dir, applies our patches there, and builds.
#
# Output: tools/refharness/instrumented/build/sauer_client
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
REF="${SAUER_REF:-/Users/fohlen/Downloads/sauerbraten-code-r6889}"
BUILD="$HERE/instrumented/build"

[ -d "$REF/src" ] || { echo "ERROR: reference src not found at '$REF/src' (set SAUER_REF)"; exit 1; }

echo "reference: $REF"
echo "build dir: $BUILD"
rm -rf "$BUILD"
mkdir -p "$BUILD"
cp -R "$REF/src/." "$BUILD/"
cd "$BUILD"

patch -p1 < "$HERE/patches/a2-trace.patch"
patch -p1 < "$HERE/patches/macos-arm64.patch"

make -j"$(sysctl -n hw.ncpu)"

echo
echo "built: $BUILD/sauer_client"

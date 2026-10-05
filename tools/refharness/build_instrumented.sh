#!/usr/bin/env bash
# Build the reference Sauerbraten client from a read-only copy of the dump.
# The dump is NEVER modified: this script copies its src/ into an ignored build
# dir, applies every patch in patches/ (in filename order), and builds.
#
# One build serves all revival tooling: A2 movement capture (SAUER_TRACE) and
# one-time OGZ metadata export (SAUER_METADATA). See patches/README.md.
#
# Output: tools/refharness/instrumented/build/sauer_client
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
PATCHES="$HERE/patches"
REF="${SAUER_REF:-/Users/fohlen/Downloads/sauerbraten-code-r6889}"
BUILD="$HERE/instrumented/build"

[ -d "$REF/src" ] || { echo "ERROR: reference src not found at '$REF/src' (set SAUER_REF)"; exit 1; }

echo "reference: $REF"
echo "build dir: $BUILD"
rm -rf "$BUILD"
mkdir -p "$BUILD"
cp -R "$REF/src/." "$BUILD/"
cd "$BUILD"

shopt -s nullglob
for patchfile in "$PATCHES"/*.patch; do
    echo "applying $(basename "$patchfile")"
    patch -p1 < "$patchfile"
done

make -j"$(sysctl -n hw.ncpu)"

echo
echo "built: $BUILD/sauer_client"

#!/usr/bin/env bash
# Build the OGZ tooling client — the one-time offline pipeline that turns classic
# Sauerbraten maps into data the revival importer can consume.
#
# This is deliberately SEPARATE from the movement reference harness
# (tools/refharness), which owns the A1/A2 physics traces. Both build a patched
# copy of the same reference dump, but they solve unrelated problems and carry
# unrelated patches, so they carry unrelated patch sets and build dirs.
#
# Like the harness, the dump is NEVER modified: this copies $SAUER_REF/src into
# tools/ogztool/out/build/, applies every patch in patches/ (filename order), and
# builds a client that supports:
#   SAUER_METADATA       export resolved map metadata as JSON (engine/metadata.cpp)
#   SAUER_METADATA_EXIT  quit right after the export (batch driver support)
#   SAUER_CONVERT_SAVE   recalc lightmaps + re-save the map as MAPVERSION (offline
#                        conversion of pre-v32 maps to v33; see patches/README.md)
#
# Output: tools/ogztool/out/sauer_client
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
PATCHES="$HERE/patches"
REF="${SAUER_REF:-/Users/fohlen/Downloads/sauerbraten-code-r6889}"
OUT="$HERE/out"
BUILD="$OUT/build"

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

cp -f sauer_client "$OUT/sauer_client"
echo
echo "built: $OUT/sauer_client"

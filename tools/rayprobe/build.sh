#!/usr/bin/env bash
# Build the raycube ground-truth probe against the ORIGINAL physics.cpp raycube.
#
# raycube/raycubeintersect/getclipplanes are file-local statics in physics.cpp,
# so rayprobe.cpp copies them verbatim (like octaprobe/ops.cpp does for the
# octaedit.cpp statics) and links octa.cpp for genclipplanes/visibletris.
set -e
cd "$(dirname "$0")"
INCL="$(cd ../../ && pwd)/tools/refharness/instrumented/build"

if [ ! -f "$INCL/engine/octa.cpp" ]; then
    echo "ERROR: instrumented reference tree not found at '$INCL'."
    echo "Build it first: tools/refharness/build_instrumented.sh"
    exit 1
fi

c++ -std=c++17 -O2 -w -o rayprobe rayprobe.cpp stubs.cpp "$INCL/engine/octa.cpp" \
    -I"$INCL/engine" -I"$INCL/shared" -I"$INCL/include" -I"$INCL" -I"$INCL/enet/include" \
    -L"$INCL/enet" -lenet

echo "built ./rayprobe — run it to dump ground truth"

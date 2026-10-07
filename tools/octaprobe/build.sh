#!/usr/bin/env bash
# Build the octree ground-truth probe against the ORIGINAL octa.cpp.
#
# The probe dumps lookupcube / subdividecube / blockcube results so the C# port
# (World/OctaEdit.cs) can be asserted against the original's exact output rather
# than hand-derived. It links octa.cpp from the instrumented reference tree and
# stubs the engine symbols octa.cpp references but the probe never calls.
set -e
cd "$(dirname "$0")"
REF="${SAUER_REF:-/Users/fohlen/Downloads/sauerbraten-code-r6889}"
INCL="$(cd ../../ && pwd)/tools/refharness/instrumented/build"

if [ ! -f "$INCL/engine/octa.cpp" ]; then
    echo "ERROR: instrumented reference tree not found at '$INCL'."
    echo "Build it first: tools/refharness/build_instrumented.sh"
    exit 1
fi

c++ -std=c++17 -O2 -w -o octaprobe probe.cpp ops.cpp stubs.cpp "$INCL/engine/octa.cpp" \
    -I"$INCL/engine" -I"$INCL/shared" -I"$INCL/include" -I"$INCL" -I"$INCL/enet/include" \
    -L"$INCL/enet" -lenet

echo "built ./octaprobe — run it to dump ground truth"

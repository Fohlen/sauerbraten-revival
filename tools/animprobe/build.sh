#!/usr/bin/env bash
# Build the MD5 animation ground-truth probe against the ORIGINAL engine types.
#
# The probe copies the md5.h anim-load + frame-compose logic verbatim (the header
# is templated and not linkable), so it only needs the reference headers (for
# quat/dualquat/boneinfo), not any engine object files.
set -e
cd "$(dirname "$0")"
INCL="$(cd ../../ && pwd)/tools/refharness/instrumented/build"

if [ ! -f "$INCL/engine/md5.h" ]; then
    echo "ERROR: instrumented reference tree not found at '$INCL'."
    echo "Build it first: tools/refharness/build_instrumented.sh"
    exit 1
fi

c++ -std=c++17 -O2 -w -o animprobe probe.cpp stubs.cpp \
    -I"$INCL/engine" -I"$INCL/shared" -I"$INCL/include" -I"$INCL" -I"$INCL/enet/include"

echo "built ./animprobe — run it to dump ground truth"

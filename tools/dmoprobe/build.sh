#!/usr/bin/env bash
# Build dmoprobe — ground-truth Sauerbraten .dmo demo parser probe
set -euo pipefail
cd "$(dirname "$0")"

c++ -std=c++17 -O2 -w -o dmoprobe dmoprobe.cpp -lz

echo "built ./dmoprobe — run with a demo file: ./dmoprobe <demo.dmo> [--json]"

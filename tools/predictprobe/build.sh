#!/usr/bin/env bash
# Build and run predictprobe — ground truth for smoothmove and predictplayer
set -euo pipefail
cd "$(dirname "$0")"

c++ -std=c++17 -O2 -o predictprobe predictprobe.cpp

echo "built ./predictprobe"
./predictprobe

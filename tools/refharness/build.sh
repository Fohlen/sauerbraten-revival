#!/usr/bin/env bash
# Build the A1 reference harness.
set -e
cd "$(dirname "$0")"
c++ -std=c++17 -O2 -Wall -Wextra -o refharness refphysics.cpp main.cpp
echo "built ./refharness"

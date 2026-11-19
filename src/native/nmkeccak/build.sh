#!/bin/sh
set -e
mkdir -p "$(dirname "$1")"
cd "$(dirname "$0")"
"${CC:-gcc}" -O3 -fPIC -shared -march=x86-64 -mtune=generic -mavx2 -mbmi -mbmi2 -fno-tree-vectorize -fno-tree-slp-vectorize \
    -fvisibility=hidden -Ixkcp nmkeccak.c xkcp/KeccakP-1600-opt64.c -o "$1"

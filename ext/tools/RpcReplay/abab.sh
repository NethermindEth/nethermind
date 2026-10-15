#!/bin/bash
# Alternating interpreter/compiled replay of a request corpus, each configuration repeated, then median ratios.
# usage: abab.sh <corpus basename> <rounds> <tag> <repeats> "<ENV=V ...>" ["<ENV=V ...>" ...]
# THRESHOLD (default 1) and EAGER (default 1) select the Legacy rules' compile mode; the corpora live in DATA
# (/c/tmp/ilevm-rpcpoc/data). A configuration with NETHERMIND_ILEVM_MODE=Adaptive runs the Adaptive rules as a node's
# startup does (live profiling of the calls unless RPCPOC_PREFER names another source), and ignores EAGER. The plugin is
# loaded from PLUGIN_DIR, this repository's build by default, rather than the copy next to the host.
HERE=$(cd "$(dirname "$0")" && pwd)
NMBIN=${NMBIN:-/c/tmp/frame-runner-nmbin}
DATA=${DATA:-/c/tmp/ilevm-rpcpoc/data}
PLUGIN_DIR=${PLUGIN_DIR:-$HERE/../../src/Nethermind.IlEvm/bin/Release/net10.0}
CORPUS=$1; ROUNDS=$2; TAG=$3; REPEATS=$4; shift 4
W=$(cygpath -w "$DATA")
HOOK="$(cygpath -w "$HERE/bin/Release/net10.0/RpcReplay.dll")"
PLUGIN="$(cygpath -w "$PLUGIN_DIR")"
for r in $(seq 1 "$REPEATS"); do
  i=0
  for cfg in "$@"; do
    i=$((i+1))
    log="$DATA/abab_${TAG}_${i}_$r.log"
    env $cfg RPCPOC_AFFINITY=${AFFINITY:-555} DOTNET_STARTUP_HOOKS="$HOOK" RPCPOC_PLUGIN_DIR="$PLUGIN" \
      RPCPOC_ARGS="$W\\$CORPUS|$W\\$CORPUS.groups.txt|$W\\abab_${TAG}_${i}_$r.csv|$ROUNDS|${THRESHOLD:-1}|${EAGER:-1}|100000000|100000|1|eth_call|0" \
      timeout 1800 "$NMBIN/nethtest.exe" --stateTest -i unused > "$log" 2>&1
  done
done
/c/Python310/python -I /c/tmp/ilevm-rpcpoc/abab_summary.py "$DATA" "$TAG" "$REPEATS" "$@"

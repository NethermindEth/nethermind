#!/usr/bin/env bash
# run-rabdi.sh DATA_DIR — a Rabdi node: Nethermind (with the Arkiv plugin, archive) and a Lighthouse beacon node.
#
# The native counterpart of db-chain-networks' networks/testnet/rabdi/run-node.sh, with Nethermind as the
# execution client: same peers (p2p.yaml), same consensus flags, and the watchers registered as trusted beacon
# peers at every start. It syncs from genesis — there is no snapshot step: every block is executed.
#
# Environment: NETWORK_DIR (a db-chain-networks networks/testnet/rabdi checkout), NETHERMIND (the Runner's output
# directory, built with the Arkiv native library; see tools/Arkiv/README.md), LIGHTHOUSE (the lighthouse binary). Ports: EL_RPC (8545), EL_ENGINE (8551), EL_P2P (30303),
# CL_HTTP (5052), CL_P2P (9000). EL_EXTRA_ARGS is appended to Nethermind's arguments (e.g. health-check thresholds on
# a large shared volume, whose free percentage says little about the space this node needs).
set -euo pipefail
data=$(realpath -m "$1"); mkdir -p "$data"/{el,cl}
: "${NETWORK_DIR:?}" "${NETHERMIND:?}" "${LIGHTHOUSE:?}"
EL_RPC=${EL_RPC:-8545}; EL_ENGINE=${EL_ENGINE:-8551}; EL_P2P=${EL_P2P:-30303}; CL_HTTP=${CL_HTTP:-5052}; CL_P2P=${CL_P2P:-9000}

[ -s "$data/jwt.hex" ] || { head -c 32 /dev/urandom | od -An -tx1 | tr -d ' \n' > "$data/jwt.hex"; chmod 600 "$data/jwt.hex"; }
el_peers=$(grep -oE 'enode://[^ ]+' "$NETWORK_DIR/p2p.yaml" | paste -sd, -)
cl_enrs=$(grep -oE 'enr:[A-Za-z0-9_-]+' "$NETWORK_DIR/p2p.yaml" | paste -sd, -)

setsid dotnet "$NETHERMIND/nethermind.dll" \
  -c rabdi -dd "$data/el" \
  --Network.StaticPeers "$el_peers" \
  --JsonRpc.JwtSecretFile "$data/jwt.hex" --JsonRpc.Port "$EL_RPC" --JsonRpc.EnginePort "$EL_ENGINE" \
  --Network.P2PPort "$EL_P2P" --Network.DiscoveryPort "$EL_P2P" ${EL_EXTRA_ARGS:-} \
  > "$data/el.log" 2>&1 &
echo $! > "$data/el.pid"

# The watchers as trusted beacon peers, re-registered until the beacon API takes them (run-node.sh's register()).
( until for enr in ${cl_enrs//,/ }; do
      curl -sf -m 5 -X POST -H 'content-type: application/json' --data "{\"enr\":\"$enr\"}" "http://127.0.0.1:$CL_HTTP/lighthouse/add_peer" >/dev/null || exit 1
    done; do sleep 5; done; echo "registered the watchers as trusted beacon peers" >> "$data/cl.log" ) &

setsid "$LIGHTHOUSE" beacon_node --testnet-dir "$NETWORK_DIR" --datadir "$data/cl" \
  --execution-endpoint "http://127.0.0.1:$EL_ENGINE" --execution-jwt "$data/jwt.hex" \
  --http --http-address 127.0.0.1 --http-port "$CL_HTTP" --port "$CL_P2P" --quic-port $((CL_P2P + 1)) \
  --boot-nodes "$cl_enrs" --disable-discovery \
  --allow-insecure-genesis-sync --ignore-ws-check --disable-optimistic-finalized-sync \
  >> "$data/cl.log" 2>&1 &
echo $! > "$data/cl.pid"
echo "EL pid $(cat "$data/el.pid"), CL pid $(cat "$data/cl.pid"); logs in $data/{el,cl}.log"

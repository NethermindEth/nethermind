#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
#
# Scenario A: a network of resource-constrained Nethermind nodes on one host, each in its own container with a CPU quota,
# a memory limit and tc netem on its egress, takes a Poisson stream of frame transactions with leanSPHINCS claims; see
# tools/Eip8288Bench/README.md. Usage:
#
#   mempool.sh <out dir> <image> [label]
#
# Settings come from the environment (defaults in brackets): NODES [6], DEGREE [5], NODE_CPUS [8] vCPUs per node (a CFS quota,
# fractional values allowed), LOAD_CPUS [1] for the load generator, AGGREGATION [Mempool] (Blocks.DependencyProofAggregation),
# NODE_MEMORY [16g], DELAY_MS [40] one-way per link, JITTER_MS [10], UP_MBIT [25], LOSS_PCT [0], RATE [5] wrappers/s,
# SECONDS_TO_RUN [120], WARMUP_SECONDS [20], GRACE_SECONDS [30], DEPS, TXS_PER_WRAPPER, DUP_FRACTION, SHARED_FRACTION,
# SEED, BENCH_BIN (published Eip8288Bench directory), POOL (signature pool, reused across runs).
set -euo pipefail

out=$(realpath -m "$1"); image=$2; label=${3:-run}
NODES=${NODES:-6} DEGREE=${DEGREE:-5} NODE_CPUS=${NODE_CPUS:-8} NODE_MEMORY=${NODE_MEMORY:-16g}
DELAY_MS=${DELAY_MS:-40} JITTER_MS=${JITTER_MS:-10} UP_MBIT=${UP_MBIT:-25} LOSS_PCT=${LOSS_PCT:-0}
RATE=${RATE:-5} SECONDS_TO_RUN=${SECONDS_TO_RUN:-120} WARMUP_SECONDS=${WARMUP_SECONDS:-20} GRACE_SECONDS=${GRACE_SECONDS:-30}
LOAD_CPUS=${LOAD_CPUS:-1} AGGREGATION=${AGGREGATION:-Mempool}
: "${BENCH_BIN:?BENCH_BIN must point at the published Eip8288Bench}"
POOL=${POOL:-$out/../signatures.bin}
net=eip8288-bench-$label
prefix=eip8288-bench-$label
helper=eip8288-bench-helper:1
mkdir -p "$out" "$(dirname "$(realpath -m "$POOL")")"

cleanup() {
  for ((i = 0; i < NODES; i++)); do docker rm -f "$prefix-$i" >/dev/null 2>&1 || true; done
  docker rm -f "$prefix-load" >/dev/null 2>&1 || true
  docker network rm "$net" >/dev/null 2>&1 || true
}
[[ -n ${KEEP:-} ]] || trap cleanup EXIT
cleanup

# Shaping and scraping run from a helper image, so the host needs only docker.
if ! docker image inspect "$helper" >/dev/null 2>&1; then
  docker build -q -t "$helper" - >/dev/null <<'EOF'
FROM debian:bookworm-slim
RUN apt-get update && apt-get install -y --no-install-recommends iproute2 curl ca-certificates && rm -rf /var/lib/apt/lists/*
EOF
fi

total_cpus=$(nproc)
need=$(awk -v n="$NODES" -v c="$NODE_CPUS" -v l="$LOAD_CPUS" 'BEGIN { print n * c + l }')
awk -v need="$need" -v have="$total_cpus" 'BEGIN { exit !(need <= have) }' ||
  { echo "::error::$NODES nodes of $NODE_CPUS vCPUs plus the load generator need $need CPUs; this host has $total_cpus"; exit 1; }

docker network create "$net" >/dev/null
cfg=$out/cfg; mkdir -p "$cfg"
openssl rand -hex 32 > "$cfg/jwt"
docker run --rm --network none -v "$BENCH_BIN:/bench:ro" -v "$cfg:/cfg" mcr.microsoft.com/dotnet/runtime:11.0 \
  dotnet /bench/Eip8288Bench.dll genesis --out=/cfg/genesis.json --accounts="${ACCOUNTS:-20000}"

for ((i = 0; i < NODES; i++)); do
  # NODE_EXTRA_FLAGS is split into separate flags on purpose.
  # shellcheck disable=SC2086
  docker run -d --name "$prefix-$i" --network "$net" --cpus "$NODE_CPUS" --memory "$NODE_MEMORY" \
    -v "$cfg:/cfg:ro" "$image" --config=none --datadir=/data --Init.ChainSpecPath=/cfg/genesis.json \
    --Init.DiscoveryEnabled=false --Init.WebSocketsEnabled=true --JsonRpc.Enabled=true --JsonRpc.Host=0.0.0.0 \
    --JsonRpc.Port=8545 --JsonRpc.WebSocketsPort=8546 --JsonRpc.EnabledModules=eth,net,admin,txpool,subscribe,web3 \
    --JsonRpc.EngineHost=0.0.0.0 --JsonRpc.EnginePort=8551 --JsonRpc.JwtSecretFile=/cfg/jwt \
    --Network.P2PPort=30303 --Network.DiscoveryPort=30303 --Network.MaxActivePeers=50 --Network.OnlyStaticPeers=true \
    --Metrics.Enabled=true --Metrics.ExposePort=9091 --TxPool.Size=65536 --TxPool.BlobsSupport=Disabled --TxPool.AcceptTxWhenNotSynced=true --HealthChecks.Enabled=false \
    --Blocks.DependencyProofAggregation="$AGGREGATION" \
    ${NODE_EXTRA_FLAGS:-} >/dev/null
done

rpc() { # rpc <node> <method> [params json]
  docker run --rm --network "$net" "$helper" curl -s --max-time 10 -H 'Content-Type: application/json' \
    -d "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"$2\",\"params\":${3:-[]}}" "http://$prefix-$1:8545"
}
for ((i = 0; i < NODES; i++)); do
  for _ in $(seq 120); do rpc "$i" web3_clientVersion | grep -q result && break; sleep 2; done
  rpc "$i" web3_clientVersion | grep -q result || { docker logs "$prefix-$i" | tail -50; echo "::error::node $i did not start"; exit 1; }
done
declare -a enodes
for ((i = 0; i < NODES; i++)); do
  ip=$(docker inspect -f "{{(index .NetworkSettings.Networks \"$net\").IPAddress}}" "$prefix-$i")
  enode=$(rpc "$i" admin_nodeInfo | sed -E 's/.*"enode":"(enode:\/\/[0-9a-f]+)@[^"]*".*/\1/')
  enodes[i]="$enode@$ip:30303"
done
# A ring plus seeded random chords gives every node about DEGREE peers on a connected graph.
RANDOM=${SEED:-8288}
for ((i = 0; i < NODES; i++)); do
  rpc "$i" admin_addPeer "[\"${enodes[(i + 1) % NODES]}\",true]" >/dev/null
  for ((k = 2; k < DEGREE; k += 2)); do
    j=$(( (i + 2 + RANDOM % (NODES - 2 > 0 ? NODES - 2 : 1)) % NODES ))
    (( j != i )) && rpc "$i" admin_addPeer "[\"${enodes[j]}\",true]" >/dev/null
  done
done

for ((i = 0; i < NODES; i++)); do
  shape="netem delay ${DELAY_MS}ms ${JITTER_MS}ms distribution normal loss ${LOSS_PCT}% rate ${UP_MBIT}mbit limit 100000"
  docker run --rm --network "container:$prefix-$i" --cap-add NET_ADMIN "$helper" sh -c "tc qdisc replace dev eth0 root $shape"
done

for _ in $(seq 60); do
  low=0
  for ((i = 0; i < NODES; i++)); do
    count=$(rpc "$i" net_peerCount | sed -nE 's/.*"result":"(0x[0-9a-f]+)".*/\1/p')
    peers=$(( ${count:-0} ))
    (( peers >= 1 )) || low=1
  done
  (( low == 0 )) && break; sleep 2
done
sleep "$WARMUP_SECONDS"

snapshot() { # snapshot <phase>
  for ((i = 0; i < NODES; i++)); do
    docker exec "$prefix-$i" sh -c 'cat /sys/fs/cgroup/cpu.stat; echo "memory_current $(cat /sys/fs/cgroup/memory.current)";
      echo "memory_peak $(cat /sys/fs/cgroup/memory.peak 2>/dev/null || echo 0)"; cat /proc/net/dev' > "$out/node$i.$1.txt"
    docker run --rm --network "$net" "$helper" curl -s --max-time 10 "http://$prefix-$i:9091/metrics" > "$out/node$i.$1.prom" || true
    rpc "$i" net_peerCount > "$out/node$i.$1.peers.json" || true
  done
}
snapshot start

rpcs=; wss=
for ((i = 0; i < NODES; i++)); do rpcs+="${rpcs:+,}http://$prefix-$i:8545"; wss+="${wss:+,}ws://$prefix-$i:8546"; done
docker run --name "$prefix-load" --network "$net" --cpus "$LOAD_CPUS" -v "$BENCH_BIN:/bench:ro" -v "$out:/out" \
  -v "$(dirname "$(realpath -m "$POOL")"):/pool" -e BENCH_NETHERMIND_COMMIT -e BENCH_LEAN_PACKAGE -e BENCH_MACHINE_TYPE \
  mcr.microsoft.com/dotnet/runtime:11.0 dotnet /bench/Eip8288Bench.dll load --rpc="$rpcs" --ws="$wss" --out=/out \
  --pool="/pool/$(basename "$POOL")" --accounts="${ACCOUNTS:-20000}" --rate="$RATE" --seconds="$SECONDS_TO_RUN" --grace-seconds="$GRACE_SECONDS" \
  ${DEPS:+--deps=$DEPS} ${DUP_FRACTION:+--dup-fraction=$DUP_FRACTION} \
  ${SHARED_FRACTION:+--shared-fraction=$SHARED_FRACTION} ${SEED:+--seed=$SEED} --sign-parallelism="$total_cpus" | tee "$out/load.txt"

snapshot end
for ((i = 0; i < NODES; i++)); do docker logs "$prefix-$i" > "$out/node$i.log" 2>&1 || true; done
cat > "$out/network.json" <<EOF
{"label":"$label","profile":"${PROFILE:-}","aggregation":"$AGGREGATION","image":"$image","imageId":"$(docker image inspect -f '{{.Id}}' "$image")","nodes":$NODES,"degree":$DEGREE,
 "nodeCpus":$NODE_CPUS,"nodeMemory":"$NODE_MEMORY","delayMs":$DELAY_MS,"jitterMs":$JITTER_MS,"upMbit":$UP_MBIT,"lossPct":$LOSS_PCT,
 "warmupSeconds":$WARMUP_SECONDS,"hostCpus":$total_cpus}
EOF
docker run --rm --network none -v "$BENCH_BIN:/bench:ro" -v "$out:/out" mcr.microsoft.com/dotnet/runtime:11.0 \
  dotnet /bench/Eip8288Bench.dll summarize --dir=/out --nodes="$NODES"

#!/usr/bin/env bash
# benchmark.sh CLIENT PAYLOADS.jsonl OUT_DIR — import the same engine_newPayloadV4 stream into one execution client
# from an empty datadir, with no peers, and record throughput, latency, peak RSS and database size.
#
# CLIENT is "nethermind" (NETHERMIND = the Runner's output directory, built with the Arkiv native library)
# or "arkiv-reth" (ARKIV_RETH = the arkiv-reth binary, built from github.com/Arkiv-Network/arkiv at the tag the
# network runs). GENESIS is the network's genesis file. Each client runs as its network deploys it: arkiv-reth
# with its default (head-only) GolemDB history, Nethermind with the archive configuration.
#
# Runs must be sequential: two clients importing at once compete for cores, memory bandwidth and disk, unevenly.
# CLIENT_CPUS (e.g. 8-23) and FEEDER_CPUS (e.g. 6-7) pin the client and the feeder with taskset, so every run gets
# the same cores; anything else on the host belongs on other cores. Besides wall time the result records the
# client's CPU time (user + system) and the host's load average, so contention can be read off each run.
set -euo pipefail
pin_client=(); [ -n "${CLIENT_CPUS:-}" ] && pin_client=(taskset -c "$CLIENT_CPUS")
pin_feeder=(); [ -n "${FEEDER_CPUS:-}" ] && pin_feeder=(taskset -c "$FEEDER_CPUS")
client=$1; payloads=$2; out=$(realpath -m "$3"); rm -rf "$out"; mkdir -p "$out"
tools=$(dirname "$(realpath "$0")")
head -c 32 /dev/urandom | od -An -tx1 | tr -d ' \n' > "$out/jwt.hex"
case "$client" in
  nethermind)
    engine=38551
    setsid "${pin_client[@]}" dotnet "$NETHERMIND/nethermind.dll" -c rabdi -dd "$out/data" \
      --Network.StaticPeers "" --Network.MaxActivePeers 0 --Init.PeerManagerEnabled false \
      --JsonRpc.JwtSecretFile "$out/jwt.hex" --JsonRpc.Port 38545 --JsonRpc.EnginePort $engine \
      --Network.P2PPort 30414 --Network.DiscoveryPort 30414 > "$out/client.log" 2>&1 &
    ;;
  arkiv-reth)
    engine=48551
    ARKIV_STORE_PATH="$out/data/golemdb" setsid "${pin_client[@]}" "$ARKIV_RETH" node --chain "$GENESIS" --datadir "$out/data" \
      --authrpc.addr 127.0.0.1 --authrpc.port $engine --authrpc.jwtsecret "$out/jwt.hex" \
      --http --http.addr 127.0.0.1 --http.port 48545 --http.api eth,net,web3 --ipcdisable \
      --port 30415 --disable-discovery --max-outbound-peers 0 --max-inbound-peers 0 > "$out/client.log" 2>&1 &
    ;;
  *) echo "unknown client $client" >&2; exit 2 ;;
esac
pid=$!
trap 'kill $pid 2>/dev/null || true' EXIT
until curl -s -m 2 -o /dev/null -X POST "http://127.0.0.1:$engine"; do kill -0 $pid || { tail -20 "$out/client.log"; exit 1; }; sleep 1; done
sleep 5
( peak=0; while kill -0 $pid 2>/dev/null; do r=$(awk '/VmHWM/{print int($2/1024)}' /proc/$pid/status 2>/dev/null || echo 0); [ "${r:-0}" -gt $peak ] && peak=$r; echo $peak > "$out/peak_rss_mb"; sleep 5; done ) &
cat /proc/loadavg > "$out/loadavg_start"
( while kill -0 $pid 2>/dev/null; do cut -d' ' -f1 /proc/loadavg >> "$out/loadavg_samples"; sleep 30; done ) &
"${pin_feeder[@]}" python3 -I "$tools/engine_feed.py" "http://127.0.0.1:$engine" "$out/jwt.hex" "$payloads" --fcu-every 1 --report "$out/feed.json" 2> "$out/feed.log"
sleep 3
tick=$(getconf CLK_TCK); read -r utime stime < <(awk '{print $14, $15}' /proc/$pid/stat)
cpu_s=$(( (utime + stime) / tick ))
kill $pid; wait $pid 2>/dev/null || true
load=$(awk '{s+=$1; n++} END {printf "%.1f", n ? s/n : 0}' "$out/loadavg_samples")
jq --arg client "$client" --argjson rss "$(cat "$out/peak_rss_mb")" --argjson db "$(du -sm "$out/data" | cut -f1)" \
  --argjson cpu "$cpu_s" --argjson load "$load" --arg cpus "${CLIENT_CPUS:-all}" \
  '. + {client: $client, peak_rss_mb: $rss, datadir_mb: $db, client_cpu_s: $cpu, cpu_ms_per_block: ((1000 * $cpu / .blocks) * 100 | round / 100), host_load_avg: $load, client_cpus: $cpus}' \
  "$out/feed.json" | tee "$out/result.json"

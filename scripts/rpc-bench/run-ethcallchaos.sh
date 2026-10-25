#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
#
# Run EthCallChaos (kamilchodola/EthCallChaos, ASP.NET app) in a .NET SDK container against a running node.

set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/rpc-bench/lib.sh
source "$HERE/lib.sh"

RPC_URL="${RPC_URL:-http://localhost:8545}"
: "${OUT_DIR:?output directory for EthCallChaos results}"
: "${SCRATCH_ROOT:?writable scratch root}"
ECC_REPO="${ECC_REPO:-https://github.com/kamilchodola/EthCallChaos.git}"
ECC_REF="${ECC_REF:-v1.0.0}"
ECC_CORPUS_DB="${ECC_CORPUS_DB:-}"
ECC_CORPUS_URL="${ECC_CORPUS_URL:-https://github.com/kamilchodola/EthCallChaos/releases/download/corpus-v2/ethcallchaos.db}"
ECC_CORPUS_SHA256="${ECC_CORPUS_SHA256:-}"
ECC_RATE="${ECC_RATE:-50}"
ECC_PARALLEL="${ECC_PARALLEL:-8}"
ECC_DURATION="${ECC_DURATION:-300}"
ECC_API_PORT="${ECC_API_PORT:-5000}"
ECC_LEADERBOARD_TOP="${ECC_LEADERBOARD_TOP:-50}"
# Tool defaults (mean>200ms, cv<0.3) qualify nothing against a fast local node.
ECC_MIN_MEAN_MS="${ECC_MIN_MEAN_MS:-1}"
ECC_MAX_CV="${ECC_MAX_CV:-10}"
SDK_IMAGE="${SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:10.0}"
CONTAINER_NAME="${ECC_CONTAINER_NAME:-ethcallchaos-bench}"

mkdir -p "$OUT_DIR"
SCRATCH_ROOT="$(realpath -m -- "$SCRATCH_ROOT")"
assert_sane_dir "$SCRATCH_ROOT" "SCRATCH_ROOT"
work="$SCRATCH_ROOT/ethcallchaos"
as_root rm -rf "$work"
mkdir -p "$work"

log "Cloning $ECC_REPO@$ECC_REF..."
git init -q "$work/src"
git -C "$work/src" remote add origin "$ECC_REPO"
git -C "$work/src" fetch -q --depth 1 origin "$ECC_REF" || die "failed to fetch $ECC_REF from $ECC_REPO"
git -C "$work/src" checkout -q FETCH_HEAD
proj_dir="$work/src/src/EthCallChaos"
[[ -d "$proj_dir" ]] || die "EthCallChaos project not found at $proj_dir"

if [[ -n "$ECC_CORPUS_DB" && -f "$ECC_CORPUS_DB" ]]; then
  cp "$ECC_CORPUS_DB" "$work/bench.db"
  log "Using provided corpus DB (copied): $ECC_CORPUS_DB"
elif [[ -n "$ECC_CORPUS_URL" ]] && curl -sfL --retry 3 -o "$work/bench.db" "$ECC_CORPUS_URL"; then
  if [[ -n "$ECC_CORPUS_SHA256" ]]; then
    echo "${ECC_CORPUS_SHA256}  $work/bench.db" | sha256sum -c - || die "corpus DB sha256 mismatch"
  fi
  log "Using corpus DB downloaded from $ECC_CORPUS_URL ($(du -h "$work/bench.db" | cut -f1))."
elif [[ -f "$proj_dir/ethcallchaos.db" ]]; then
  cp "$proj_dir/ethcallchaos.db" "$work/bench.db"
  log "Using corpus DB committed in the repo (copied)."
else
  : > "$work/bench.db"
  log "::warning::No corpus DB found — EthCallChaos starts from a fresh corpus."
fi

docker rm -f "$CONTAINER_NAME" >/dev/null 2>&1 || true
log "Launching EthCallChaos via $SDK_IMAGE (rate=$ECC_RATE/s, parallel=$ECC_PARALLEL, duration=${ECC_DURATION}s)..."
docker run -d --name "$CONTAINER_NAME" \
  --network host \
  -v "$work:/work" \
  -w /work/src/src/EthCallChaos \
  -e "ASPNETCORE_ENVIRONMENT=Production" \
  -e "ASPNETCORE_URLS=http://127.0.0.1:${ECC_API_PORT}" \
  -e "Kestrel__Endpoints__Http__Url=http://127.0.0.1:${ECC_API_PORT}" \
  -e "DOTNET_CLI_HOME=/work" \
  -e "NUGET_PACKAGES=/work/.nuget" \
  -e "Rpc__NodeUrl=${RPC_URL}" \
  -e "Rpc__MaxCallsPerSecond=${ECC_RATE}" \
  -e "Rpc__MaxParallelCalls=${ECC_PARALLEL}" \
  -e "Validation__MinMeanThresholdMs=${ECC_MIN_MEAN_MS}" \
  -e "Validation__MaxCoefficientOfVariation=${ECC_MAX_CV}" \
  -e "ConnectionStrings__Sqlite=Data Source=/work/bench.db" \
  -e "Storage__ConnectionString=Data Source=/work/bench.db" \
  "$SDK_IMAGE" bash -lc "dotnet run -c Release" \
  || die "failed to launch EthCallChaos container"

api="http://localhost:${ECC_API_PORT}"
log "Waiting for EthCallChaos API at $api/api/stats (build + start can take a few minutes)..."
elapsed=0
until curl -sf "$api/api/stats" >/dev/null 2>&1; do
  if ! docker ps --format '{{.Names}}' | grep -qx "$CONTAINER_NAME"; then
    docker logs "$CONTAINER_NAME" 2>&1 | tail -n 120 || true
    die "EthCallChaos container exited before the API came up"
  fi
  sleep 5
  elapsed=$((elapsed + 5))
  if (( elapsed >= 900 )); then
    docker logs "$CONTAINER_NAME" 2>&1 | tail -n 120 || true
    die "EthCallChaos API never came up within 900s"
  fi
done
log "API up after ${elapsed}s. Generating load for ${ECC_DURATION}s..."
sleep "$ECC_DURATION"

scrape_failed=0
curl -sf "$api/api/stats" -o "$OUT_DIR/stats.json" || { log "::warning::failed to scrape /api/stats"; scrape_failed=1; }
curl -sf "$api/api/leaderboard?top=${ECC_LEADERBOARD_TOP}&sortBy=mean_ms" -o "$OUT_DIR/leaderboard.json" \
  || { log "::warning::failed to scrape /api/leaderboard"; scrape_failed=1; }
docker logs "$CONTAINER_NAME" > "$OUT_DIR/ethcallchaos.log" 2>&1 || true

docker stop -t 20 "$CONTAINER_NAME" >/dev/null 2>&1 || true
if [[ -s "$work/bench.db" ]]; then
  cp "$work/bench.db" "$OUT_DIR/ethcallchaos.db" 2>/dev/null \
    && log "Saved evolved corpus -> $OUT_DIR/ethcallchaos.db ($(du -h "$OUT_DIR/ethcallchaos.db" | cut -f1))" \
    || log "::warning::failed to persist evolved corpus DB"
fi
docker rm -f "$CONTAINER_NAME" >/dev/null 2>&1 || true

summary="$OUT_DIR/ethcallchaos-summary.md"
{
  echo "## RPC Benchmark — EthCallChaos"
  echo
  echo "Node: \`$RPC_URL\` | rate: \`${ECC_RATE}/s\` | parallel: \`$ECC_PARALLEL\` | duration: \`${ECC_DURATION}s\`"
  echo
  if [[ -s "$OUT_DIR/stats.json" ]]; then
    echo "### Stats"
    echo '```json'
    jq . "$OUT_DIR/stats.json" 2>/dev/null || cat "$OUT_DIR/stats.json"
    echo '```'
    echo
  fi
  if [[ -s "$OUT_DIR/leaderboard.json" ]]; then
    echo "### Slowest eth_call cases (top ${ECC_LEADERBOARD_TOP}, by mean ms)"
    echo
    echo "| Rank | mean ms | p99 ms | to | calldata |"
    echo "|---:|---:|---:|---|---|"
    jq -r '.[] | "| \(.rankPosition // .RankPosition // "-") | \(.meanMs // .MeanMs // "-") | \(.p99Ms // .P99Ms // "-") | \(.toAddress // .ToAddress // "-") | \(.calldataPreview // .CalldataPreview // "-") |"' \
      "$OUT_DIR/leaderboard.json" 2>/dev/null | head -n "$ECC_LEADERBOARD_TOP" || true
  fi
} > "$summary"
log "EthCallChaos summary written to $summary"

[[ "$scrape_failed" == "0" ]] || die "EthCallChaos results could not be scraped (container log is in the artifact)"
jq -e . "$OUT_DIR/stats.json" >/dev/null 2>&1 || die "EthCallChaos /api/stats response is empty or not valid JSON"
jq -e . "$OUT_DIR/leaderboard.json" >/dev/null 2>&1 || die "EthCallChaos /api/leaderboard response is empty or not valid JSON"

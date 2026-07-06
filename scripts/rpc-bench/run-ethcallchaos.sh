#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
#
# Run the EthCallChaos tool (kamilchodola/EthCallChaos) against an already-running
# JSON-RPC node. EthCallChaos is an ASP.NET app with no CLI: it is launched inside
# a .NET SDK container, configured via environment variables, left to hammer the
# node for a fixed duration, then its HTTP API is scraped for results.
#
# A pre-built corpus DB (SQLite) lets it replay representative eth_call workloads
# instead of evolving from scratch. The corpus DB is COPIED to scratch so the
# original stays pristine; it is the EthCallChaos tool's own DB, entirely separate
# from the Nethermind state DB.
#
# Tool-specific knobs are read from env (the workflow fills them from the
# `tool_config` JSON): ECC_REF, ECC_CORPUS_DB, ECC_RATE, ECC_PARALLEL,
# ECC_DURATION, ECC_API_PORT, ECC_LEADERBOARD_TOP.

set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/rpc-bench/lib.sh
source "$HERE/lib.sh"

RPC_URL="${RPC_URL:-http://localhost:8545}"
: "${OUT_DIR:?output directory for EthCallChaos results}"
: "${SCRATCH_ROOT:?writable scratch root}"
ECC_REPO="${ECC_REPO:-https://github.com/kamilchodola/EthCallChaos.git}"
# Pin to a specific commit so a push to the repo's default branch cannot silently
# change benchmark results (or run unreviewed code on the runner). Override
# ECC_REF (sha/tag/branch) or ECC_REPO to move it.
ECC_REF="${ECC_REF:-6c31f78097545cc2ec3265ce187a2c777a75b1d7}"
ECC_CORPUS_DB="${ECC_CORPUS_DB:-}"          # optional path on the runner to a pristine corpus DB
ECC_CORPUS_URL="${ECC_CORPUS_URL:-https://github.com/kamilchodola/EthCallChaos/releases/download/corpus-v1/ethcallchaos.db}"
ECC_RATE="${ECC_RATE:-50}"                  # -> Rpc__MaxCallsPerSecond
ECC_PARALLEL="${ECC_PARALLEL:-8}"           # -> Rpc__MaxParallelCalls
ECC_DURATION="${ECC_DURATION:-300}"         # seconds of load
ECC_API_PORT="${ECC_API_PORT:-5000}"
ECC_LEADERBOARD_TOP="${ECC_LEADERBOARD_TOP:-50}"
# The tool's default 'confirmed slow' gates (mean > 200ms, cv < 0.3) are tuned
# for hunting pathological cases on remote nodes; against a fast local snapshot
# node nothing qualifies and the leaderboard stays empty. CI defaults rank the
# slowest validated cases regardless: mean > 1ms, cv gate effectively off.
ECC_MIN_MEAN_MS="${ECC_MIN_MEAN_MS:-1}"
ECC_MAX_CV="${ECC_MAX_CV:-10}"
SDK_IMAGE="${SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:10.0}"
CONTAINER_NAME="${ECC_CONTAINER_NAME:-ethcallchaos-bench}"

mkdir -p "$OUT_DIR"
SCRATCH_ROOT="$(realpath -m -- "$SCRATCH_ROOT")"
assert_sane_dir "$SCRATCH_ROOT" "SCRATCH_ROOT"
work="$SCRATCH_ROOT/ethcallchaos"
# The SDK container may have left root-owned files in scratch on a prior run.
as_root rm -rf "$work"
mkdir -p "$work"

# ---------------------------------------------------------------------------
# Fetch the tool source.
# ---------------------------------------------------------------------------
log "Cloning $ECC_REPO@$ECC_REF..."
# Shallow-fetch a single ref. This accepts a commit sha, tag, or branch (GitHub
# serves reachable commit shas), unlike 'git clone --branch' which rejects a
# bare sha — required so ECC_REF can default to a pinned commit.
git init -q "$work/src"
git -C "$work/src" remote add origin "$ECC_REPO"
git -C "$work/src" fetch -q --depth 1 origin "$ECC_REF" \
  || die "failed to fetch $ECC_REF from $ECC_REPO"
git -C "$work/src" checkout -q FETCH_HEAD

proj_dir="$work/src/src/EthCallChaos"
[[ -d "$proj_dir" ]] || die "EthCallChaos project not found at $proj_dir"

# ---------------------------------------------------------------------------
# Resolve the corpus DB (copied so the source corpus stays pristine).
# Precedence: runner-local path > release URL > repo-committed > fresh.
# ---------------------------------------------------------------------------
if [[ -n "$ECC_CORPUS_DB" && -f "$ECC_CORPUS_DB" ]]; then
  cp "$ECC_CORPUS_DB" "$work/bench.db"
  log "Using provided corpus DB (copied): $ECC_CORPUS_DB"
elif [[ -n "$ECC_CORPUS_URL" ]] && curl -sfL --retry 3 -o "$work/bench.db" "$ECC_CORPUS_URL"; then
  log "Using corpus DB downloaded from $ECC_CORPUS_URL ($(du -h "$work/bench.db" | cut -f1))."
elif [[ -f "$proj_dir/ethcallchaos.db" ]]; then
  cp "$proj_dir/ethcallchaos.db" "$work/bench.db"
  log "Using corpus DB committed in the repo (copied)."
else
  : > "$work/bench.db"
  log "::warning::No corpus DB found — EthCallChaos will start from a fresh corpus (slower warmup, less representative)."
fi

# ---------------------------------------------------------------------------
# Launch EthCallChaos inside a .NET SDK container (host network so it reaches
# the node on localhost and exposes its API on the host).
# ---------------------------------------------------------------------------
docker rm -f "$CONTAINER_NAME" >/dev/null 2>&1 || true
log "Launching EthCallChaos via $SDK_IMAGE (rate=$ECC_RATE/s, parallel=$ECC_PARALLEL, duration=${ECC_DURATION}s)..."
docker run -d --name "$CONTAINER_NAME" \
  --network host \
  -v "$work:/work" \
  -w /work/src/src/EthCallChaos \
  -e "ASPNETCORE_ENVIRONMENT=Production" \
  -e "ASPNETCORE_URLS=http://0.0.0.0:${ECC_API_PORT}" \
  -e "Kestrel__Endpoints__Http__Url=http://0.0.0.0:${ECC_API_PORT}" \
  -e "DOTNET_CLI_HOME=/work" \
  -e "NUGET_PACKAGES=/work/.nuget" \
  -e "Rpc__NodeUrl=${RPC_URL}" \
  -e "Rpc__MaxCallsPerSecond=${ECC_RATE}" \
  -e "Rpc__MaxParallelCalls=${ECC_PARALLEL}" \
  -e "Validation__MinMeanThresholdMs=${ECC_MIN_MEAN_MS}" \
  -e "Validation__MaxCoefficientOfVariation=${ECC_MAX_CV}" \
  -e "ConnectionStrings__Sqlite=Data Source=/work/bench.db" \
  -e "Storage__ConnectionString=Data Source=/work/bench.db" \
  "$SDK_IMAGE" \
  bash -lc "dotnet run -c Release" \
  || die "failed to launch EthCallChaos container"

# ---------------------------------------------------------------------------
# Wait for the API, run for the configured duration, scrape results.
# ---------------------------------------------------------------------------
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

log "Scraping results..."
curl -sf "$api/api/stats" -o "$OUT_DIR/stats.json" || log "::warning::failed to scrape /api/stats"
curl -sf "$api/api/leaderboard?top=${ECC_LEADERBOARD_TOP}&sortBy=mean_ms" -o "$OUT_DIR/leaderboard.json" || log "::warning::failed to scrape /api/leaderboard"

docker logs "$CONTAINER_NAME" > "$OUT_DIR/ethcallchaos.log" 2>&1 || true
docker rm -f "$CONTAINER_NAME" >/dev/null 2>&1 || true

# ---------------------------------------------------------------------------
# Build a markdown summary.
# ---------------------------------------------------------------------------
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
    # ASP.NET minimal APIs serialize with camelCase; keep PascalCase fallback.
    jq -r '.[] | "| \(.rankPosition // .RankPosition // "-") | \(.meanMs // .MeanMs // "-") | \(.p99Ms // .P99Ms // "-") | \(.toAddress // .ToAddress // "-") | \(.calldataPreview // .CalldataPreview // "-") |"' \
      "$OUT_DIR/leaderboard.json" 2>/dev/null | head -n "$ECC_LEADERBOARD_TOP" || true
  fi
} > "$summary"

log "EthCallChaos summary written to $summary"

#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
#
# Run the `flood` load-testing tool (kamilchodola fork, Vegeta backend) against a
# single already-running JSON-RPC node and collect per-test reports.
#
# Tool-specific knobs are read from env (the workflow fills them from the
# `tool_config` JSON): RATES, DURATION, DEEP_CHECK, TESTS, LABEL, EXTRA_ARGS.

set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/rpc-bench/lib.sh
source "$HERE/lib.sh"

RPC_URL="${RPC_URL:-http://localhost:8545}"
: "${OUT_DIR:?output directory for flood results}"
LABEL="${LABEL:-nethermind}"
FLOOD_REPO="${FLOOD_REPO:-git+https://github.com/kamilchodola/flood.git}"
VEGETA_VERSION="${VEGETA_VERSION:-12.11.1}"
RATES="${RATES:-10 100 500}"
DURATION="${DURATION:-30}"
DEEP_CHECK="${DEEP_CHECK:-false}"
TESTS="${TESTS:-}"               # space/comma-separated subset; empty = all Single Load Tests
EXTRA_ARGS="${EXTRA_ARGS:-}"

mkdir -p "$OUT_DIR"
export PATH="$HOME/.local/bin:$PATH"

# ---------------------------------------------------------------------------
# Install Vegeta (HTTP load generator used by flood).
# ---------------------------------------------------------------------------
if ! command -v vegeta >/dev/null 2>&1; then
  log "Installing vegeta $VEGETA_VERSION..."
  tmp="$(mktemp -d)"
  curl -sSL "https://github.com/tsenart/vegeta/releases/download/v${VEGETA_VERSION}/vegeta_${VEGETA_VERSION}_linux_amd64.tar.gz" -o "$tmp/vegeta.tgz"
  tar -xzf "$tmp/vegeta.tgz" -C "$tmp" vegeta
  if as_root install -m0755 "$tmp/vegeta" /usr/local/bin/vegeta 2>/dev/null; then
    log "  vegeta installed to /usr/local/bin"
  else
    mkdir -p "$HOME/.local/bin"
    install -m0755 "$tmp/vegeta" "$HOME/.local/bin/vegeta"
    log "  vegeta installed to ~/.local/bin"
  fi
fi
vegeta --version || true

# ---------------------------------------------------------------------------
# Install flood (kamilchodola fork). Prefer uv (guaranteed on this runner by
# the expb workflow); fall back to pip like rpc-comparison.yml.
# ---------------------------------------------------------------------------
log "Installing flood from $FLOOD_REPO..."
if command -v uv >/dev/null 2>&1; then
  # No explicit package name: uv infers it from the git source and exposes the
  # `flood` entry point regardless of the package's distribution name.
  uv tool install --force "$FLOOD_REPO"
  export PATH="$(uv tool dir --bin):$PATH"
else
  python3 -m pip install --user --force-reinstall "$FLOOD_REPO" \
    || python3 -m pip install --user --break-system-packages --force-reinstall "$FLOOD_REPO"
fi
command -v flood >/dev/null 2>&1 || die "flood not on PATH after install"

# ---------------------------------------------------------------------------
# Resolve the list of tests to run.
# ---------------------------------------------------------------------------
if [[ -z "$TESTS" ]]; then
  log "No test filter given — discovering all Single Load Tests via 'flood ls'..."
  mapfile -t test_list < <(
    flood ls \
      | sed -n '/Single Load Tests/,/Multi Load Tests/{/Single Load Tests\|Multi Load Tests\|───/d; s/- //p}' \
      | sed 's/[[:space:]]//g' \
      | sed '/^$/d'
  )
else
  IFS=', ' read -r -a test_list <<< "$TESTS"
fi
[[ "${#test_list[@]}" -gt 0 ]] || die "no flood tests resolved (filter='$TESTS')"
log "Will run ${#test_list[@]} flood test(s): ${test_list[*]}"

deep=""
[[ "$DEEP_CHECK" == "true" ]] && deep="--deep-check"

# ---------------------------------------------------------------------------
# Run each test as a single-node load test.
# ---------------------------------------------------------------------------
for t in "${test_list[@]}"; do
  [[ -z "$t" ]] && continue
  od="$OUT_DIR/$t"
  log "flood $t ${LABEL}=$RPC_URL --rates $RATES --duration $DURATION $deep --output $od"
  # shellcheck disable=SC2086
  flood "$t" "${LABEL}=$RPC_URL" \
    --rates $RATES \
    --duration "$DURATION" \
    $deep \
    --output "$od" \
    $EXTRA_ARGS 2>&1 | tee "$OUT_DIR/${t}.log" \
    || log "::warning::flood test '$t' exited non-zero (continuing)"
done

# ---------------------------------------------------------------------------
# Build a markdown summary from `flood report`.
# ---------------------------------------------------------------------------
summary="$OUT_DIR/flood-summary.md"
{
  echo "## RPC Benchmark — flood (Vegeta load test)"
  echo
  echo "Node: \`$RPC_URL\` | rates: \`$RATES\` req/s | duration: \`${DURATION}s\` | deep-check: \`$DEEP_CHECK\`"
  echo
  echo "Tests: ${test_list[*]}"
  echo
  echo '```'
} > "$summary"

for t in "${test_list[@]}"; do
  od="$OUT_DIR/$t"
  [[ -d "$od" ]] || continue
  {
    echo "=== $t ==="
    flood report "$od" 2>&1 || true
    echo
  } >> "$summary"
done
echo '```' >> "$summary"

log "flood summary written to $summary"

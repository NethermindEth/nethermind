#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
#
# Run flood (kamilchodola fork, Vegeta backend) against a running node; REFERENCE_RPC_URL switches to equality mode.

set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/rpc-bench/lib.sh
source "$HERE/lib.sh"

RPC_URL="${RPC_URL:-http://localhost:8545}"
: "${OUT_DIR:?output directory for flood results}"
LABEL="${LABEL:-nethermind}"
REFERENCE_RPC_URL="${REFERENCE_RPC_URL:-}"
REFERENCE_LABEL="${REFERENCE_LABEL:-reference}"
[[ -n "$REFERENCE_RPC_URL" && "$REFERENCE_LABEL" == "$LABEL" ]] && REFERENCE_LABEL="${REFERENCE_LABEL}-ref"
FLOOD_REPO="${FLOOD_REPO:-git+https://github.com/kamilchodola/flood.git}"
FLOOD_REF="${FLOOD_REF:-bd0d8e4e3d698cf5b5f141c2a36d86f5f5b5e1ef}"
VEGETA_VERSION="${VEGETA_VERSION:-12.11.1}"
VEGETA_SHA256="${VEGETA_SHA256:-1dbdb525fe82e084626e02e73405eb386a3ed1a894426e22f440f6565b3e5d17}"
RATES="${RATES:-10 100 500}"
DURATION="${DURATION:-30}"
DEEP_CHECK="${DEEP_CHECK:-false}"
TESTS="${TESTS:-}"
EXTRA_ARGS="${EXTRA_ARGS:-}"

mkdir -p "$OUT_DIR"
export PATH="$HOME/.local/bin:$PATH"

if ! command -v vegeta >/dev/null 2>&1; then
  log "Installing vegeta $VEGETA_VERSION..."
  tmp="$(mktemp -d)"
  curl -sSfL "https://github.com/tsenart/vegeta/releases/download/v${VEGETA_VERSION}/vegeta_${VEGETA_VERSION}_linux_amd64.tar.gz" -o "$tmp/vegeta.tgz"
  echo "${VEGETA_SHA256}  $tmp/vegeta.tgz" | sha256sum -c - || die "vegeta tarball sha256 mismatch"
  tar -xzf "$tmp/vegeta.tgz" -C "$tmp" vegeta
  as_root install -m0755 "$tmp/vegeta" /usr/local/bin/vegeta 2>/dev/null \
    || { mkdir -p "$HOME/.local/bin"; install -m0755 "$tmp/vegeta" "$HOME/.local/bin/vegeta"; }
fi
vegeta --version || true

flood_spec="$FLOOD_REPO"
[[ -n "$FLOOD_REF" && "$FLOOD_REPO" != *@* ]] && flood_spec="${FLOOD_REPO}@${FLOOD_REF}"
log "Installing flood from $flood_spec..."
if command -v uv >/dev/null 2>&1; then
  # py3.10/3.11: flood's pyarrow pin has no newer wheels; lxml-html-clean restores lxml.html.clean.
  uv tool install --force --python 3.10 --with lxml-html-clean "$flood_spec" \
    || uv tool install --force --python 3.11 --with lxml-html-clean "$flood_spec"
  uv_bin="$(uv tool dir --bin)"
  export PATH="$uv_bin:$PATH"
else
  python3 -m pip install --user --force-reinstall "$flood_spec" \
    || python3 -m pip install --user --break-system-packages --force-reinstall "$flood_spec"
fi
command -v flood >/dev/null 2>&1 || die "flood not on PATH after install"

if [[ -n "$TESTS" ]]; then
  IFS=', ' read -r -a test_list <<< "$TESTS"
elif [[ -n "$REFERENCE_RPC_URL" ]]; then
  test_list=(all)
else
  mapfile -t test_list < <(flood ls \
    | sed -n '/Single Load Tests/,/Multi Load Tests/{/Single Load Tests\|Multi Load Tests\|───/d; s/- //p}' \
    | sed 's/[[:space:]]//g; /^$/d')
fi
[[ "${#test_list[@]}" -gt 0 ]] || die "no flood tests resolved (filter='$TESTS')"
log "Will run ${#test_list[@]} flood test(s): ${test_list[*]}"

deep=""
[[ "$DEEP_CHECK" == "true" ]] && deep="--deep-check"
read -ra extra_args_arr <<< "$EXTRA_ARGS"
[[ -z "$REFERENCE_RPC_URL" ]] || assert_same_head "$RPC_URL" "$REFERENCE_RPC_URL"

# The fork's reporter can crash after results.json is written, so completeness of results gates the step, not exit codes.
run_failures=0
for t in "${test_list[@]}"; do
  [[ -z "$t" ]] && continue
  if [[ -n "$REFERENCE_RPC_URL" ]]; then
    log "flood $t ${LABEL}=$RPC_URL ${REFERENCE_LABEL}=$REFERENCE_RPC_URL --equality"
    flood "$t" "${LABEL}=$RPC_URL" "${REFERENCE_LABEL}=$REFERENCE_RPC_URL" --equality \
      ${extra_args_arr[@]+"${extra_args_arr[@]}"} 2>&1 | tee "$OUT_DIR/${t}.log" \
      || log "::warning::flood equality test '$t' exited non-zero (may just signal differences)"
  else
    log "flood $t ${LABEL}=$RPC_URL --rates $RATES --duration $DURATION $deep --output $OUT_DIR/$t"
    # shellcheck disable=SC2086
    flood "$t" "${LABEL}=$RPC_URL" --rates $RATES --duration "$DURATION" $deep --output "$OUT_DIR/$t" \
      ${extra_args_arr[@]+"${extra_args_arr[@]}"} 2>&1 | tee "$OUT_DIR/${t}.log" \
      || { log "::warning::flood test '$t' exited non-zero (continuing)"; run_failures=$((run_failures + 1)); }
  fi
done

summary="$OUT_DIR/flood-summary.md"
if [[ -n "$REFERENCE_RPC_URL" ]]; then
  {
    echo "## RPC Comparison — flood equality (differential test)"
    echo
    echo "\`$LABEL\` = \`$RPC_URL\` vs \`$REFERENCE_LABEL\` = \`$REFERENCE_RPC_URL\`"
    echo
  } > "$summary"
  missing=0
  for t in "${test_list[@]}"; do
    tlog="$OUT_DIR/${t}.log"
    {
      echo "### $t"
      echo
      if [[ -s "$tlog" ]]; then
        if strip_ansi "$tlog" | grep -qiE 'mismatch|not equal|✖|✗|FAILED'; then
          echo "> :warning: **response differences detected** — see \`${t}.log\` in the artifact."
          echo
        fi
        echo "<details><summary>flood output (last 120 lines)</summary>"
        echo
        echo '```'
        strip_ansi "$tlog" | tail -n 120
        echo '```'
        echo
        echo "</details>"
      else
        echo "**NO OUTPUT** — flood wrote no log for this test."
        missing=$((missing + 1))
      fi
      echo
    } >> "$summary"
  done
  log "flood equality summary written to $summary"
  (( missing == 0 )) || die "flood produced no output for ${missing} of ${#test_list[@]} equality tests"
  exit 0
fi

{
  echo "## RPC Benchmark — flood (Vegeta load test)"
  echo
  echo "Node: \`$RPC_URL\` | rates: \`$RATES\` req/s | duration: \`${DURATION}s\` | deep-check: \`$DEEP_CHECK\`"
  echo
} > "$summary"
missing=0
parse_failures=0
for t in "${test_list[@]}"; do
  od="$OUT_DIR/$t"
  {
    echo "### $t"
    echo
    if [[ -f "$od/results.json" ]]; then
      echo "| node | rate (rps) | actual rate | success | mean (ms) | p50 (ms) | p90 (ms) | p99 (ms) | max (ms) | requests |"
      echo "|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|"
      jq -r '
        def ms(x): (x * 100000 | round) / 100;
        .results | to_entries[] | .key as $node | .value as $r
        | range($r.target_rate | length) as $i
        | "| \($node) | \($r.target_rate[$i]) | \(($r.actual_rate[$i] * 100 | round) / 100) | \(($r.success[$i] * 100) | round)% | \(ms($r.mean[$i])) | \(ms($r.p50[$i])) | \(ms($r.p90[$i])) | \(ms($r.p99[$i])) | \(ms($r.max[$i])) | \($r.requests[$i]) |"
      ' "$od/results.json" 2>/dev/null \
        || { echo; echo "Failed to render $od/results.json"; parse_failures=$((parse_failures + 1)); }
    else
      echo "**NO RESULTS** — flood did not write \`results.json\` (see \`${t}.log\` in the artifact)."
      missing=$((missing + 1))
    fi
    echo
  } >> "$summary"
done
log "flood summary written to $summary"
(( run_failures == 0 )) || log "::warning::${run_failures} flood invocation(s) exited non-zero — results.json completeness gates the step"
fail_msgs=()
(( missing > 0 ))        && fail_msgs+=("${missing} of ${#test_list[@]} test(s) produced no results.json")
(( parse_failures > 0 )) && fail_msgs+=("${parse_failures} results.json failed to render")
(( ${#fail_msgs[@]} == 0 )) || die "flood benchmark incomplete: ${fail_msgs[*]}"

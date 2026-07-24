#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
#
# Run the `flood` load-testing tool (kamilchodola fork, Vegeta backend) against
# an already-running JSON-RPC node and collect per-test reports. When
# REFERENCE_RPC_URL is set, runs flood's EQUALITY (differential) mode instead:
# the same requests go to both nodes and their responses are compared.
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
REFERENCE_RPC_URL="${REFERENCE_RPC_URL:-}"   # non-empty switches to equality mode
REFERENCE_LABEL="${REFERENCE_LABEL:-reference}"
# flood addresses nodes by name — keep the two labels distinct (NM-vs-NM runs).
[[ -n "$REFERENCE_RPC_URL" && "$REFERENCE_LABEL" == "$LABEL" ]] && REFERENCE_LABEL="${REFERENCE_LABEL}-ref"
FLOOD_REPO="${FLOOD_REPO:-git+https://github.com/kamilchodola/flood.git}"
# Pin the fork to a specific commit so a push to its default branch cannot
# silently change CI behavior. Override FLOOD_REF (sha/tag/branch) or FLOOD_REPO.
FLOOD_REF="${FLOOD_REF:-bd0d8e4e3d698cf5b5f141c2a36d86f5f5b5e1ef}"
VEGETA_VERSION="${VEGETA_VERSION:-12.11.1}"
# sha256 of vegeta_${VEGETA_VERSION}_linux_amd64.tar.gz from the official release
# checksums; if you bump VEGETA_VERSION, update this too (or override both).
VEGETA_SHA256="${VEGETA_SHA256:-1dbdb525fe82e084626e02e73405eb386a3ed1a894426e22f440f6565b3e5d17}"
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
  curl -sSfL "https://github.com/tsenart/vegeta/releases/download/v${VEGETA_VERSION}/vegeta_${VEGETA_VERSION}_linux_amd64.tar.gz" -o "$tmp/vegeta.tgz"
  echo "${VEGETA_SHA256}  $tmp/vegeta.tgz" | sha256sum -c - \
    || die "vegeta tarball sha256 mismatch (expected ${VEGETA_SHA256}) — refusing to install an unverified binary"
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
# Pin to FLOOD_REF unless the repo spec already carries its own '@<ref>'.
flood_spec="$FLOOD_REPO"
[[ -n "$FLOOD_REF" && "$FLOOD_REPO" != *@* ]] && flood_spec="${FLOOD_REPO}@${FLOOD_REF}"
log "Installing flood from $flood_spec..."
if command -v uv >/dev/null 2>&1; then
  # Pin Python 3.10: flood's 2023-era pins (checkthechain -> pyarrow 12.0.1)
  # only have prebuilt wheels up to cp311; newer interpreters force source
  # builds that fail. lxml-html-clean restores the lxml.html.clean module that
  # lxml 5.x split out (flood's results pipeline imports it via unpinned deps).
  # No explicit package name: uv infers it from the git source and exposes the
  # `flood` entry point regardless of the dist name.
  uv tool install --force --python 3.10 --with lxml-html-clean "$flood_spec" \
    || uv tool install --force --python 3.11 --with lxml-html-clean "$flood_spec"
  uv_bin="$(uv tool dir --bin)"
  export PATH="$uv_bin:$PATH"
else
  python3 -m pip install --user --force-reinstall "$flood_spec" \
    || python3 -m pip install --user --break-system-packages --force-reinstall "$flood_spec"
fi
command -v flood >/dev/null 2>&1 || die "flood not on PATH after install"

# ---------------------------------------------------------------------------
# Resolve the list of tests to run.
# ---------------------------------------------------------------------------
if [[ -z "$TESTS" ]]; then
  if [[ -n "$REFERENCE_RPC_URL" ]]; then
    # 'all' is flood's built-in run-every-equality-test name.
    test_list=(all)
  else
    log "No test filter given — discovering all Single Load Tests via 'flood ls'..."
    mapfile -t test_list < <(
      flood ls \
        | sed -n '/Single Load Tests/,/Multi Load Tests/{/Single Load Tests\|Multi Load Tests\|───/d; s/- //p}' \
        | sed 's/[[:space:]]//g' \
        | sed '/^$/d'
    )
  fi
else
  IFS=', ' read -r -a test_list <<< "$TESTS"
fi
[[ "${#test_list[@]}" -gt 0 ]] || die "no flood tests resolved (filter='$TESTS')"
log "Will run ${#test_list[@]} flood test(s): ${test_list[*]}"

deep=""
[[ "$DEEP_CHECK" == "true" ]] && deep="--deep-check"

# Word-split EXTRA_ARGS into an array so multiple flags still pass through as
# separate args, but WITHOUT filename globbing — a literal '*'/'?' in a
# user-supplied flag must not expand against the current directory.
read -ra extra_args_arr <<< "$EXTRA_ARGS"

# ---------------------------------------------------------------------------
# Run each test — single-node load test, or two-node equality (differential)
# test when a reference node is configured. flood rejects --output/--rates/
# --duration in equality mode (results go to stdout, captured per-test).
# ---------------------------------------------------------------------------
if [[ -n "$REFERENCE_RPC_URL" ]]; then
  # Diffing nodes at different heads is meaningless ('latest' diverges).
  assert_same_head "$RPC_URL" "$REFERENCE_RPC_URL"
fi

# Non-zero invocations are tallied for a warning but do NOT fail the step on
# their own: the fork's report/print stage is known to crash AFTER results.json
# is written (toolstr API drift — the reason the summary renders from
# results.json), so the exit code is not a reliable failure signal. What gates
# the step is results completeness: every test must produce a parseable
# results.json. Equality mode likewise gates on captured output, since its exit
# code may merely signal response differences.
run_failures=0
for t in "${test_list[@]}"; do
  [[ -z "$t" ]] && continue
  od="$OUT_DIR/$t"
  if [[ -n "$REFERENCE_RPC_URL" ]]; then
    log "flood $t ${LABEL}=$RPC_URL ${REFERENCE_LABEL}=$REFERENCE_RPC_URL --equality"
    flood "$t" "${LABEL}=$RPC_URL" "${REFERENCE_LABEL}=$REFERENCE_RPC_URL" \
      --equality \
      ${extra_args_arr[@]+"${extra_args_arr[@]}"} 2>&1 | tee "$OUT_DIR/${t}.log" \
      || log "::warning::flood equality test '$t' exited non-zero (may just signal differences — see the summary)"
  else
    log "flood $t ${LABEL}=$RPC_URL --rates $RATES --duration $DURATION $deep --output $od"
    # $RATES and $deep are intentionally word-split (SC2086); EXTRA_ARGS is passed
    # via an array so its flags word-split but do not glob-expand.
    # shellcheck disable=SC2086
    flood "$t" "${LABEL}=$RPC_URL" \
      --rates $RATES \
      --duration "$DURATION" \
      $deep \
      --output "$od" \
      ${extra_args_arr[@]+"${extra_args_arr[@]}"} 2>&1 | tee "$OUT_DIR/${t}.log" \
      || { log "::warning::flood test '$t' exited non-zero (continuing)"; run_failures=$((run_failures + 1)); }
  fi
done

# ---------------------------------------------------------------------------
# Build a markdown summary. Load tests render directly from results.json
# (robust against the pretty-printer's loosely-pinned dependencies; vegeta
# latencies are seconds); equality tests only print to stdout, so their
# captured logs are embedded instead.
# ---------------------------------------------------------------------------
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
        # Surface mismatches loudly, then embed the (ANSI-stripped) tool output.
        if sed -E 's/\x1B\[[0-9;?]*[ -/]*[@-~]//g' "$tlog" | grep -qiE 'mismatch|not equal|✖|✗|FAILED'; then
          echo "> :warning: **response differences detected** — see the output below and \`${t}.log\` in the artifact."
          echo
        fi
        echo "<details><summary>flood output (last 120 lines)</summary>"
        echo
        echo '```'
        sed -E 's/\x1B\[[0-9;?]*[ -/]*[@-~]//g' "$tlog" | tail -n 120
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
  if (( missing > 0 )); then
    die "flood produced no output for ${missing} of ${#test_list[@]} equality tests — failing the benchmark step"
  fi
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
if (( run_failures > 0 )); then
  log "::warning::${run_failures} flood invocation(s) exited non-zero — expected (post-results reporter crash); results.json completeness is what gates the step"
fi
# A partial benchmark must not publish as success — fail (after the summary is
# written) when any test's results are missing or unparseable.
fail_msgs=()
(( missing > 0 ))        && fail_msgs+=("${missing} of ${#test_list[@]} test(s) produced no results.json")
(( parse_failures > 0 )) && fail_msgs+=("${parse_failures} results.json failed to render")
if (( ${#fail_msgs[@]} > 0 )); then
  die "flood benchmark incomplete: ${fail_msgs[*]} — failing the benchmark step"
fi

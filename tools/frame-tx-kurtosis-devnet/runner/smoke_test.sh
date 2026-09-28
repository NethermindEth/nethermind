#!/usr/bin/env bash
# End-to-end smoke test. Runs one short scenario and then asserts the acceptance criteria
# against what was actually collected, rather than reporting that it ran.
#
#   runner/smoke_test.sh                 # builds images for the smoke ceiling, then runs
#   runner/smoke_test.sh --no-build      # images already built
#   runner/smoke_test.sh --keep          # leave the enclave up for inspection
#
# Proves: both clients healthy, blocks produced, baseline frame transactions included, the
# attacker path exercised, the ceiling live on both clients, raw metrics and results captured.
set -uo pipefail

readonly HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly DEVNET_ROOT="$(cd "${HERE}/.." && pwd)"

CEILING="${SMOKE_CEILING:-235800}"
SCENARIO_ID="smoke-c${CEILING}"
BUILD=1
KEEP=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --no-build) BUILD=0; shift ;;
    --keep) KEEP="--keep"; shift ;;
    -h|--help) sed -n '2,12p' "${BASH_SOURCE[0]}" | sed 's/^# \?//'; exit 0 ;;
    *) echo "unknown flag $1" >&2; exit 1 ;;
  esac
done

for tool in kurtosis docker python3; do
  if ! command -v "${tool}" >/dev/null; then
    echo "error: ${tool} is not installed. See the README prerequisites." >&2
    exit 1
  fi
done
if ! docker info >/dev/null 2>&1; then
  echo "error: the Docker daemon is not reachable." >&2
  exit 1
fi

if [[ "${BUILD}" == "1" ]]; then
  echo "=== building images for ceiling ${CEILING} ==="
  "${DEVNET_ROOT}/images/build.sh" "${CEILING}" || exit 1
fi

# Run inside the traffic image: it carries the pinned encoder and the signing dependencies, so
# the checks cover exactly what the generator will run and the host needs no Python packages.
echo "=== offline shape checks (in the traffic image) ==="
docker run --rm --entrypoint python frame-tx-devnet/traffic:local tests/test_shapes.py || exit 1

echo "=== running the smoke scenario ==="
python3 "${HERE}/run_scenario.py" \
  --ceiling "${CEILING}" \
  --attacker-role keccak-wide \
  --k-retry 1 \
  --attacker-rate 5 \
  --baseline-rate 1 \
  --warmup 24 \
  --duration 120 \
  --scenario-id "${SCENARIO_ID}" \
  ${KEEP}
run_status=$?

readonly OUT_DIR="${DEVNET_ROOT}/results/${SCENARIO_ID}"
readonly RESULTS="${OUT_DIR}/${SCENARIO_ID}.result"

echo
echo "=== acceptance checks ==="
failures=0

check() {
  local name="$1" condition="$2" detail="${3:-}"
  if [[ "${condition}" == "1" ]]; then
    printf '  [ok  ] %s\n' "${name}"
  else
    printf '  [FAIL] %s%s\n' "${name}" "${detail:+ -- ${detail}}"
    failures=$((failures + 1))
  fi
}

if [[ ! -f "${RESULTS}" ]]; then
  echo "  [FAIL] no result file at ${RESULTS}"
  exit 1
fi

# Both clients answered before anything was timed.
ready_nodes=$(grep -c 'case=preflight .*ready=yes' "${RESULTS}" || true)
check "both execution clients became ready" "$([[ ${ready_nodes} -ge 2 ]] && echo 1 || echo 0)" \
  "${ready_nodes} node(s) reported ready"

# Blocks are being produced, seen independently per node.
producing=$(awk '/case=head_progress/ { for (i=1;i<=NF;i++) if ($i ~ /^blocks=/) { split($i,a,"="); if (a[2]+0 > 1) c++ } } END { print c+0 }' "${RESULTS}")
check "both nodes advanced past one block" "$([[ ${producing} -ge 2 ]] && echo 1 || echo 0)" \
  "${producing} node(s) advanced"

# Frame transactions actually became valid on both clients before anything was measured.
fork_open=$(grep -c 'case=fork_gate .*active=yes' "${RESULTS}" || true)
check "frame transactions became valid on both clients" \
  "$([[ ${fork_open} -ge 2 ]] && echo 1 || echo 0)" "${fork_open} client(s) opened"

# The ceiling is live on both implementations.
probes=$(grep -c 'case=ceiling_probe .*rejected=yes' "${RESULTS}" || true)
check "the ceiling rejected an over-budget prefix on both clients" \
  "$([[ ${probes} -ge 2 ]] && echo 1 || echo 0)" "${probes} client(s) rejected it"

# Baseline frame transactions reached blocks.
included=$(awk '/case=inclusion .*role=baseline/ { for (i=1;i<=NF;i++) if ($i ~ /^included=/) { split($i,a,"="); t+=a[2] } } END { print t+0 }' "${RESULTS}")
check "baseline frame transactions were included" "$([[ ${included} -ge 1 ]] && echo 1 || echo 0)" \
  "${included} inclusion(s)"

# The attacker path ran and was refused, which is the code path under measurement.
rejected=$(awk '/case=admission .*role=keccak-wide/ { for (i=1;i<=NF;i++) if ($i ~ /^rejected=/) { split($i,a,"="); t+=a[2] } } END { print t+0 }' "${RESULTS}")
check "the attacker role was exercised and refused" "$([[ ${rejected} -ge 1 ]] && echo 1 || echo 0)" \
  "${rejected} rejection(s)"

# Raw capture landed.
check "metrics were snapshotted" \
  "$([[ -d ${OUT_DIR}/metrics && -n $(ls -A "${OUT_DIR}/metrics" 2>/dev/null) ]] && echo 1 || echo 0)"
check "run provenance was written" "$([[ -s ${OUT_DIR}/run.json ]] && echo 1 || echo 0)"
check "per-submission events were written" \
  "$([[ -s ${OUT_DIR}/${SCENARIO_ID}.events.jsonl ]] && echo 1 || echo 0)"

echo
if [[ ${failures} -gt 0 ]]; then
  echo "${failures} acceptance check(s) failed. Raw output: ${OUT_DIR}"
  exit 1
fi
if [[ ${run_status} -ne 0 ]]; then
  echo "acceptance checks passed but the scenario exited ${run_status}; see ${OUT_DIR}/traffic.log"
  exit "${run_status}"
fi
echo "smoke test passed. Raw output: ${OUT_DIR}"

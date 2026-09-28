#!/usr/bin/env bash
# End-to-end smoke test: runs one short scenario, then checks what was collected.
#
#   runner/smoke_test.sh                                  # keccak-wide at 235800, builds images first
#   runner/smoke_test.sh --no-build                       # images already built
#   runner/smoke_test.sh --role signature-stuffed --no-build
#   runner/smoke_test.sh --role soispoke-groth16 --groth16-artifacts ~/frame-verify-gas-v2 --no-build
#   runner/smoke_test.sh --ceiling 500000 --rate 10 --no-build
#   runner/smoke_test.sh --keep                           # leave the enclave up for inspection
#
# Passes only if every execution client, whatever its implementation, activated EIP-8141,
# refused a prefix just over the ceiling, refused every attack transaction, included every
# baseline transaction, logged no exception, ran the image built for this ceiling, and agreed
# with the other clients on the chain.
set -uo pipefail

readonly HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly DEVNET_ROOT="$(cd "${HERE}/.." && pwd)"

CEILING="235800"
ROLE="keccak-wide"
RATE="5"
GROTH16=""
BUILD=1
KEEP=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --no-build) BUILD=0; shift ;;
    --keep) KEEP="--keep"; shift ;;
    --ceiling) CEILING="${2:?--ceiling needs a value}"; shift 2 ;;
    --role) ROLE="${2:?--role needs a value}"; shift 2 ;;
    --rate) RATE="${2:?--rate needs a value}"; shift 2 ;;
    --groth16-artifacts) GROTH16="${2:?--groth16-artifacts needs a path}"; shift 2 ;;
    -h|--help) sed -n '2,15p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "unknown flag $1" >&2; exit 1 ;;
  esac
done

readonly SCENARIO_ID="smoke-c${CEILING}-${ROLE}"

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

# In the traffic image: it carries the pinned encoder, so the checks cover exactly what the
# generator will send and the host needs no Python packages.
echo "=== offline shape checks (in the traffic image) ==="
docker run --rm --entrypoint python frame-tx-devnet/traffic:local tests/test_shapes.py >/dev/null || {
  echo "offline shape checks failed; run them without >/dev/null to see which" >&2
  exit 1
}
echo "ok"

echo "=== running ${SCENARIO_ID} ==="
extra=()
[[ -n "${GROTH16}" ]] && extra+=(--groth16-artifacts "${GROTH16}")
[[ -n "${KEEP}" ]] && extra+=("${KEEP}")
python3 "${HERE}/run_scenario.py" \
  --ceiling "${CEILING}" \
  --attacker-role "${ROLE}" \
  --attacker-rate "${RATE}" \
  --baseline-rate 1 \
  --warmup 24 \
  --duration 120 \
  --scenario-id "${SCENARIO_ID}" \
  ${extra[@]+"${extra[@]}"}
run_status=$?

readonly OUT_DIR="${DEVNET_ROOT}/results/${SCENARIO_ID}"
echo
echo "=== acceptance checks ==="
python3 "${HERE}/check_results.py" "${OUT_DIR}" "${ROLE}" || exit 1
if [[ ${run_status} -ne 0 ]]; then
  echo "checks passed but the scenario exited ${run_status}; see ${OUT_DIR}/traffic.log"
  exit "${run_status}"
fi
echo "smoke test passed. Raw output: ${OUT_DIR}"

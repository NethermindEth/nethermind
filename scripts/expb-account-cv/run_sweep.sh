#!/usr/bin/env bash
set -euo pipefail

: "${EXPB_PYTHON:?EXPB_PYTHON is required}"
: "${EXPB_CONFIG:?EXPB_CONFIG is required}"
: "${EXPB_RUN_ROOT:?EXPB_RUN_ROOT is required}"
: "${EXPB_RESULTS_ROOT:?EXPB_RESULTS_ROOT is required}"
: "${EXPB_SAMPLES_ROOT:?EXPB_SAMPLES_ROOT is required}"
: "${EXPB_ACCOUNT_HELPER_PATH:?EXPB_ACCOUNT_HELPER_PATH is required}"

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
mkdir -p "${EXPB_RUN_ROOT}" "${EXPB_RESULTS_ROOT}" "${EXPB_SAMPLES_ROOT}"
unset EXPB_EVM_WARMUP DOTTRACE PERF DOTNET_TRACE NETHERMIND_PROFILE_BLOCKS || true

labels=(master forced-interpolation auto-cv-0.1 auto-cv-0.2 auto-cv-0.5)
for label in "${labels[@]}"; do
  for name in "expb-executor-${label//./-}" "expb-executor-${label//./-}-nethermind"; do
    if docker inspect "${name}" >/dev/null 2>&1; then
      echo "refusing to reuse pre-existing unowned container ${name}" >&2
      exit 2
    fi
  done
done

sampler_args=(
  "${HERE}/resource_sampler.py"
  --output-dir "${EXPB_SAMPLES_ROOT}"
  --interval 5
  --arm master expb-executor-master-nethermind
  --arm forced-interpolation expb-executor-forced-interpolation-nethermind
  --arm auto-cv-0.1 expb-executor-auto-cv-0-1-nethermind
  --arm auto-cv-0.2 expb-executor-auto-cv-0-2-nethermind
  --arm auto-cv-0.5 expb-executor-auto-cv-0-5-nethermind
)
"${EXPB_PYTHON}" "${sampler_args[@]}" >"${EXPB_RUN_ROOT}/resource-sampler.log" 2>&1 &
sampler_pid=$!

cleanup() {
  if kill -0 "${sampler_pid}" 2>/dev/null; then
    kill -TERM "${sampler_pid}" 2>/dev/null || true
    wait "${sampler_pid}" 2>/dev/null || true
  fi
}
trap cleanup EXIT

for label in "${labels[@]}"; do
  arm_dir="${EXPB_RESULTS_ROOT}/${label}"
  mkdir -p "${arm_dir}"
  log_path="${arm_dir}/expb.log"
  export EXPB_ACCOUNT_ARM="${label}"
  export EXPB_ACCOUNT_SCRATCH_ROOT="${EXPB_RUN_ROOT}"
  export EXPB_ACCOUNT_RESULTS_ROOT="${EXPB_RESULTS_ROOT}"
  set +e
  "${EXPB_PYTHON}" "${HERE}/expb_account_cv.py" execute-scenarios \
    --config-file "${EXPB_CONFIG}" \
    --filter "^${label}$" \
    --per-payload-metrics \
    --per-payload-metrics-logs \
    --print-logs \
    --no-evm-warmup \
    --no-dottrace \
    --no-dotnet-trace \
    --no-perf 2>&1 | tee "${log_path}"
  expb_status=${PIPESTATUS[0]}
  set -e
  if [[ "${expb_status}" -ne 0 ]]; then
    echo "Account CV arm ${label} failed; stopping before the next arm." >&2
    exit "${expb_status}"
  fi
  "${EXPB_PYTHON}" "${HERE}/analyze_arm.py" \
    --arm "${label}" \
    --log "${log_path}" \
    --samples "${EXPB_SAMPLES_ROOT}/${label}.csv" \
    --output "${arm_dir}/result.json"
done

"${EXPB_PYTHON}" "${HERE}/aggregate.py" \
  --results-root "${EXPB_RESULTS_ROOT}" \
  --config "${EXPB_CONFIG}" \
  --output "${EXPB_RUN_ROOT}/aggregate.json"

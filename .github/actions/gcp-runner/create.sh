#!/usr/bin/env bash
set -euo pipefail

# shellcheck source=lib.sh
. "$(dirname "$0")/lib.sh"

INSTANCE_NAME=$(derive_instance_name "$RUNNER_LABEL")
echo "instance name: ${INSTANCE_NAME}"

# An empty or non-numeric count would silently attach zero Local SSDs and leave the job
# running on the boot disk until it fills, hours into a sync.
if ! [[ "$LOCAL_SSD_COUNT" =~ ^[0-9]+$ ]]; then
  echo "::error title=GCP runner::local_ssd_count must be a non-negative integer, got '${LOCAL_SSD_COUNT}'"
  exit 1
fi

JIT_FILE="${RUNNER_TEMP}/jitconfig.b64"
RUNNER_ID=""

cleanup_failed() {
  local zone="${1:-}"
  # A create call can fail after the instance exists (timeout, partial failure), so fall
  # back to a lookup rather than leaving it for --max-run-duration to reap hours later.
  # The lookup must not be fatal: the runner registration and credential file below still
  # have to be cleaned up even when it fails.
  if [ -z "$zone" ]; then
    zone=$(resolve_zone "$INSTANCE_NAME") || zone=""
  fi
  if [ -n "$zone" ]; then
    echo "::group::serial console output"
    gcloud compute instances get-serial-port-output "$INSTANCE_NAME" \
      --project="$PROJECT_ID" --zone="$zone" 2>/dev/null || true
    echo "::endgroup::"
    gcloud compute instances delete "$INSTANCE_NAME" --project="$PROJECT_ID" \
      --zone="$zone" --quiet --delete-disks=all 2>/dev/null || true
  fi
  if [ -n "$RUNNER_ID" ]; then
    gh api --method DELETE \
      "/repos/${GITHUB_REPOSITORY}/actions/runners/${RUNNER_ID}" >/dev/null 2>&1 || true
  fi
  rm -f "$JIT_FILE"
}

response=$(jq -n \
    --arg name "$INSTANCE_NAME" \
    --argjson group "$RUNNER_GROUP_ID" \
    --arg label "$RUNNER_LABEL" \
    '{name: $name, runner_group_id: $group, labels: [$label], work_folder: "_work"}' \
  | gh api --method POST \
      "/repos/${GITHUB_REPOSITORY}/actions/runners/generate-jitconfig" \
      -H 'X-GitHub-Api-Version: 2022-11-28' --input -)

RUNNER_ID=$(jq -r '.runner.id' <<<"$response")
echo "::add-mask::$(jq -r '.encoded_jit_config' <<<"$response")"
umask 077
jq -r '.encoded_jit_config' <<<"$response" > "$JIT_FILE"
umask 022
unset response
echo "JIT runner id: ${RUNNER_ID}"

build_create_args() {
  local model="$1"
  CREATE_ARGS=(
    compute instances create "$INSTANCE_NAME"
    --project="$PROJECT_ID"
    --machine-type="$MACHINE_TYPE"
    --image-family="$IMAGE_FAMILY"
    --image-project="$IMAGE_PROJECT"
    --boot-disk-size="${BOOT_DISK_SIZE}GB"
    --boot-disk-type="$BOOT_DISK_TYPE"
    --boot-disk-device-name="$INSTANCE_NAME"
    --network="$NETWORK"
    --subnet="$SUBNET"
    --tags="$NETWORK_TAG"
    --service-account="$RUNNER_SERVICE_ACCOUNT"
    --scopes=https://www.googleapis.com/auth/logging.write,https://www.googleapis.com/auth/monitoring.write
    --shielded-secure-boot
    --shielded-vtpm
    --shielded-integrity-monitoring
    --provisioning-model="$model"
    --max-run-duration="$MAX_RUN_DURATION"
    --instance-termination-action=DELETE
    --no-restart-on-failure
    --metadata="enable-guest-attributes=TRUE,serial-port-logging-enable=TRUE,runner-version=${RUNNER_VERSION},data-mount=${DATA_MOUNT},local-ssd-count=${LOCAL_SSD_COUNT}"
    --metadata-from-file="startup-script=$(dirname "$0")/startup-script.sh,runner-jit-config=${JIT_FILE}"
    --labels="gh-run-id=${GITHUB_RUN_ID},gh-run-attempt=${GITHUB_RUN_ATTEMPT},managed-by=gcp-runner-action"
    --format=json
  )

  # Spot cannot live-migrate. On STANDARD the default MIGRATE is deliberate: Local SSD
  # survives live migration, and a multi-hour sync should not die to host maintenance.
  if [ "$model" = SPOT ]; then
    CREATE_ARGS+=(--maintenance-policy=TERMINATE)
  fi

  # Machine families whose names end in -lssd carry a fixed Local SSD complement and
  # reject the flag outright.
  if [[ "$MACHINE_TYPE" != *-lssd ]]; then
    local i
    for ((i = 0; i < LOCAL_SSD_COUNT; i++)); do
      CREATE_ARGS+=(--local-ssd=interface=NVME)
    done
  fi
}

RETRYABLE='ZONE_RESOURCE_POOL_EXHAUSTED|RESOURCE_POOL_EXHAUSTED|does not have enough resources|resource availability|currently unavailable|No available zone'
FATAL='QUOTA_EXCEEDED|Quota .* exceeded|PERMISSION_DENIED|Required .* permission'

MODELS=("$PROVISIONING_MODEL")
if [ "$PROVISIONING_MODEL" = SPOT ] && [ "$SPOT_FALLBACK_TO_STANDARD" = true ]; then
  MODELS+=(STANDARD)
fi

IFS=',' read -ra ZONE_LIST <<<"$ZONES"
CHOSEN_ZONE=""
MODEL_USED=""
INSTANCE_JSON=""

for model in "${MODELS[@]}"; do
  build_create_args "$model"
  for zone in "${ZONE_LIST[@]}"; do
    zone="${zone//[[:space:]]/}"
    [ -n "$zone" ] || continue
    echo "::group::create ${INSTANCE_NAME} in ${zone} (${model})"
    set +e
    out=$(gcloud "${CREATE_ARGS[@]}" --zone="$zone" 2>"${RUNNER_TEMP}/create.err")
    rc=$?
    set -e
    err=$(cat "${RUNNER_TEMP}/create.err")
    printf '%s\n' "$err"
    echo "::endgroup::"

    if [ $rc -eq 0 ]; then
      CHOSEN_ZONE="$zone"
      MODEL_USED="$model"
      INSTANCE_JSON="$out"
      break 2
    fi
    if grep -qE "$FATAL" <<<"$err"; then
      echo "::error title=GCP runner::non-retryable create failure: ${err}"
      cleanup_failed
      exit 1
    fi
    if grep -qE "$RETRYABLE" <<<"$err"; then
      echo "::notice title=GCP runner::${zone} has no ${model} capacity, trying next"
      # A create can also fail after the insert succeeded (operation timeout, a partial
      # failure attaching SSDs). Without this, a later zone succeeding would leave two
      # instances sharing a name and destroy.sh could delete the wrong one.
      gcloud compute instances delete "$INSTANCE_NAME" --project="$PROJECT_ID" \
        --zone="$zone" --quiet --delete-disks=all >/dev/null 2>&1 || true
      continue
    fi
    echo "::error title=GCP runner::unrecognised create failure in ${zone}: ${err}"
    cleanup_failed
    exit 1
  done
done

if [ -z "$CHOSEN_ZONE" ]; then
  echo "::error title=GCP runner::no capacity for ${MACHINE_TYPE} in any of ${ZONES}"
  cleanup_failed
  exit 1
fi

if [ "$MODEL_USED" != "$PROVISIONING_MODEL" ]; then
  echo "::warning title=GCP runner::fell back to ${MODEL_USED} (no ${PROVISIONING_MODEL} capacity)"
fi

INSTANCE_IP=$(jq -r '(if type == "array" then .[0] else . end)
  | .networkInterfaces[0].accessConfigs[0].natIP // empty' <<<"$INSTANCE_JSON")
echo "created ${INSTANCE_NAME} in ${CHOSEN_ZONE} as ${MODEL_USED}, ip ${INSTANCE_IP}"

{
  echo "instance_name=${INSTANCE_NAME}"
  echo "zone=${CHOSEN_ZONE}"
  echo "instance_ip=${INSTANCE_IP}"
  echo "runner_id=${RUNNER_ID}"
  echo "provisioning_model_used=${MODEL_USED}"
} >> "$GITHUB_OUTPUT"

deadline=$((SECONDS + BOOT_TIMEOUT))
last_vm_check=0

while :; do
  code=$(curl -sS -o "${RUNNER_TEMP}/runner.json" -w '%{http_code}' \
    -H "Authorization: Bearer ${GH_TOKEN}" \
    -H 'Accept: application/vnd.github+json' \
    -H 'X-GitHub-Api-Version: 2022-11-28' \
    "https://api.github.com/repos/${GITHUB_REPOSITORY}/actions/runners/${RUNNER_ID}" || echo 000)

  if [ "$code" = 200 ] && [ "$(jq -r .status "${RUNNER_TEMP}/runner.json")" = online ]; then
    echo "runner ${RUNNER_LABEL} is online"
    rm -f "$JIT_FILE"
    exit 0
  fi
  if [ "$code" = 404 ]; then
    echo "::error title=GCP runner::JIT runner ${RUNNER_ID} disappeared before coming online"
    cleanup_failed "$CHOSEN_ZONE"
    exit 1
  fi

  if ((SECONDS - last_vm_check >= 30)); then
    last_vm_check=$SECONDS
    vm_status=$(gcloud compute instances describe "$INSTANCE_NAME" --project="$PROJECT_ID" \
      --zone="$CHOSEN_ZONE" --format='value(status)' 2>/dev/null || echo NOT_FOUND)
    case "$vm_status" in
      RUNNING) ;;
      *)
        echo "::error title=GCP runner::VM entered ${vm_status} before the runner registered"
        cleanup_failed "$CHOSEN_ZONE"
        exit 1
        ;;
    esac
    boot_stage=$(gcloud compute instances get-guest-attributes "$INSTANCE_NAME" \
      --project="$PROJECT_ID" --zone="$CHOSEN_ZONE" --query-path=gh-runner/stage \
      --format='value(value)' 2>/dev/null || true)
    echo "startup stage: ${boot_stage:-<none yet>}"
    if [ "$boot_stage" = failed ]; then
      boot_error=$(gcloud compute instances get-guest-attributes "$INSTANCE_NAME" \
        --project="$PROJECT_ID" --zone="$CHOSEN_ZONE" --query-path=gh-runner/error \
        --format='value(value)' 2>/dev/null || true)
      echo "::error title=GCP runner::startup script failed: ${boot_error}"
      cleanup_failed "$CHOSEN_ZONE"
      exit 1
    fi
  fi

  if ((SECONDS >= deadline)); then
    echo "::error title=GCP runner::timed out after ${BOOT_TIMEOUT}s waiting for the runner"
    cleanup_failed "$CHOSEN_ZONE"
    exit 1
  fi
  sleep 15
done

#!/usr/bin/env bash
set -euo pipefail

# shellcheck source=lib.sh
. "$(dirname "$0")/lib.sh"

INSTANCE_NAME=$(derive_instance_name "$RUNNER_LABEL")
echo "instance name: ${INSTANCE_NAME}"

ZONE=$(resolve_zone "$INSTANCE_NAME")
PREEMPTED=false
DELETE_FAILED=false

if [ -n "$ZONE" ]; then
  echo "deleting ${INSTANCE_NAME} in ${ZONE}"
  # This is the last line of defence for the whole design, so a failed delete has to be
  # loud rather than leaving the VM to --max-run-duration hours later.
  if gcloud compute instances delete "$INSTANCE_NAME" --project="$PROJECT_ID" \
       --zone="$ZONE" --quiet --delete-disks=all; then
    TERMINATED_BY=deleted-by-action
  else
    DELETE_FAILED=true
    TERMINATED_BY=delete-failed
    echo "::error title=GCP runner::failed to delete ${INSTANCE_NAME} in ${ZONE}; --max-run-duration will reap it"
  fi
else
  # The instance is already gone. Every in-guest preemption signal died with it, but zone
  # operations outlive the instance, so they are the only usable post-mortem.
  echo "${INSTANCE_NAME} no longer exists, classifying from zone operations"
  ops=$(gcloud compute operations list --project="$PROJECT_ID" --zones="$ZONES" \
    --filter="targetLink~/instances/${INSTANCE_NAME}$" \
    --format='value(operationType)' 2>/dev/null || true)
  echo "operations found: ${ops:-<none>}"

  if grep -qx 'compute.instances.preempted' <<<"$ops"; then
    PREEMPTED=true
    TERMINATED_BY=preempted
    echo "::warning title=GCP runner::${RUNNER_LABEL} was preempted — infrastructure reclaim, not a sync failure"
  elif grep -qx 'delete' <<<"$ops"; then
    # The expected path: the sync job released its own VM via destroy-self.
    TERMINATED_BY=deleted-by-self
    echo "${INSTANCE_NAME} was already released by its sync job"
  elif [ -z "$ops" ]; then
    TERMINATED_BY=unknown
    echo "::warning title=GCP runner::${INSTANCE_NAME} vanished with no operations recorded"
  else
    TERMINATED_BY=terminated
    echo "::warning title=GCP runner::${INSTANCE_NAME} was terminated externally (${ops//$'\n'/, })"
  fi
fi

# Reaps a JIT runner that registered but never picked up a job — the one-shot runner
# deregisters itself after running one, so normally this finds nothing. Resolved by name
# rather than by a create-step output, because matrix strategies clobber job outputs.
if [ -n "${GH_TOKEN:-}" ]; then
  runner_id=$(gh api --paginate \
    "/repos/${GITHUB_REPOSITORY}/actions/runners" \
    -q ".runners[] | select(.name == \"${INSTANCE_NAME}\") | .id" 2>/dev/null | head -n 1 || true)
  if [ -n "$runner_id" ]; then
    echo "removing leftover runner registration ${runner_id}"
    gh api --method DELETE \
      "/repos/${GITHUB_REPOSITORY}/actions/runners/${runner_id}" >/dev/null 2>&1 || true
  fi
fi

{
  echo "instance_name=${INSTANCE_NAME}"
  echo "zone=${ZONE}"
  echo "preempted=${PREEMPTED}"
  echo "terminated_by=${TERMINATED_BY}"
} >> "$GITHUB_OUTPUT"

[ "$DELETE_FAILED" = true ] && exit 1
exit 0

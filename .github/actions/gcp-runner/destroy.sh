#!/usr/bin/env bash
set -euo pipefail

# shellcheck source=lib.sh
. "$(dirname "$0")/lib.sh"

INSTANCE_NAME=$(derive_instance_name "$RUNNER_LABEL")
echo "instance name: ${INSTANCE_NAME}"

PREEMPTED=false
DELETE_FAILED=false

# A failed lookup must not be mistaken for an absent instance, or a transient API error
# would let this job report a still-running VM as reaped. Retry once, then give up loudly.
ZONE=""
if ! ZONE=$(resolve_zone "$INSTANCE_NAME"); then
  sleep 5
  if ! ZONE=$(resolve_zone "$INSTANCE_NAME"); then
    echo "::error title=GCP runner::could not determine whether ${INSTANCE_NAME} still exists; not treating it as reaped"
    {
      echo "instance_name=${INSTANCE_NAME}"
      echo "zone="
      echo "preempted=false"
      echo "terminated_by=lookup-failed"
    } >> "$GITHUB_OUTPUT"
    exit 1
  fi
fi

if [ -n "$ZONE" ]; then
  echo "deleting ${INSTANCE_NAME} in ${ZONE}"
  if gcloud compute instances delete "$INSTANCE_NAME" --project="$PROJECT_ID" \
       --zone="$ZONE" --quiet --delete-disks=all; then
    TERMINATED_BY=deleted-by-action
  else
    # This job is a sweeper, so it routinely races the sync job's own destroy-self and a
    # delete already in flight makes the call fail while still reaching the desired state.
    # Confirm against the end state rather than the exit code; only a VM that is still
    # there afterwards is a genuine leak worth failing over.
    echo "delete call failed, checking whether ${INSTANCE_NAME} is gone anyway"
    still_there=""
    for _ in 1 2 3 4 5 6 7 8 9 10 11 12; do
      still_there=$(gcloud compute instances describe "$INSTANCE_NAME" --project="$PROJECT_ID" \
        --zone="$ZONE" --format='value(name)' 2>/dev/null || true)
      [ -z "$still_there" ] && break
      sleep 5
    done
    if [ -z "$still_there" ]; then
      TERMINATED_BY=deleted-concurrently
      echo "${INSTANCE_NAME} was already being deleted and is now gone"
    else
      DELETE_FAILED=true
      TERMINATED_BY=delete-failed
      echo "::error title=GCP runner::failed to delete ${INSTANCE_NAME} in ${ZONE}; --max-run-duration will reap it"
    fi
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

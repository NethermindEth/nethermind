#!/usr/bin/env bash

# Derives the instance name from the runner label alone, so the destroy path needs no
# state from the create job — matrix strategies clobber job outputs across entries.
# GITHUB_RUN_ATTEMPT is part of the name because generate-jitconfig returns 409 for a
# name that already exists, which would break every workflow re-run.
derive_instance_name() {
  local label="$1" sanitized hash
  sanitized=$(printf '%s' "$label" | tr '[:upper:]_' '[:lower:]-' | tr -cd 'a-z0-9-')
  hash=$(printf '%s' "$label" | sha1sum | cut -c1-8)
  printf 'gh-%s-%s-a%s' "${sanitized:0:40}" "$hash" "${GITHUB_RUN_ATTEMPT:-1}"
}

# Prints the zone, or nothing when the instance does not exist. Returns non-zero only when
# the lookup itself failed: callers must not read an empty result as "already gone", or a
# transient API error would let a live VM be reported as reaped.
resolve_zone() {
  local name="$1" out
  if ! out=$(gcloud compute instances list --project="$PROJECT_ID" \
      --filter="name=${name}" --format='value(zone.basename())' --limit=1 2>/dev/null); then
    return 1
  fi
  printf '%s' "$out"
}

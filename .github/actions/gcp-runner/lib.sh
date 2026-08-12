#!/usr/bin/env bash

# Derives the instance name from the runner label alone, so the destroy path needs no
# state from the create job — matrix strategies clobber job outputs across entries.
# GITHUB_RUN_ATTEMPT is part of the name because generate-jitconfig returns 409 for a
# name that already exists, which would break every workflow re-run.
derive_instance_name() {
  local label="$1" sanitized hash
  sanitized=$(printf '%s' "$label" | tr '[:upper:]_' '[:lower:]-' | tr -cd 'a-z0-9-')
  hash=$(printf '%s' "$label" | sha1sum | cut -c1-8)
  local name="gh-${sanitized:0:40}-${hash}-a${GITHUB_RUN_ATTEMPT:-1}"
  printf '%s' "${name%-}"
}

resolve_zone() {
  local name="$1"
  gcloud compute instances list --project="$PROJECT_ID" \
    --filter="name=${name}" --format='value(zone.basename())' --limit=1 2>/dev/null
}

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

# Quota is separate from fatal because it is per-region: another region may still work.
RETRYABLE_CREATE_ERR='ZONE_RESOURCE_POOL_EXHAUSTED|RESOURCE_POOL_EXHAUSTED|does not have enough resources|resource availability|currently unavailable|No available zone'
QUOTA_CREATE_ERR='QUOTA_EXCEEDED|Quota .* exceeded'
FATAL_CREATE_ERR='PERMISSION_DENIED|Required .* permission'

# Rotates a comma-separated zone list by a hash of the seed, keeping each region's zones
# together, so concurrent creates spread out. Deterministic: a re-run repeats the order.
order_zones() {
  local seed="$1" zones="$2"
  local -a regions=() zone_list=()
  local zone region

  IFS=',' read -ra zone_list <<<"$zones"
  local -A by_region=()
  for zone in "${zone_list[@]}"; do
    zone="${zone//[[:space:]]/}"
    [ -n "$zone" ] || continue
    region="${zone%-*}"
    if [ -z "${by_region[$region]+x}" ]; then
      regions+=("$region")
      by_region[$region]="$zone"
    else
      by_region[$region]+=" $zone"
    fi
  done
  [ "${#regions[@]}" -gt 0 ] || return 0

  local offset=$((0x$(printf '%s' "$seed" | sha1sum | cut -c1-4)))
  local -a out=()
  local i j region_zones
  for ((i = 0; i < ${#regions[@]}; i++)); do
    region="${regions[$(((i + offset) % ${#regions[@]}))]}"
    IFS=' ' read -ra region_zones <<<"${by_region[$region]}"
    for ((j = 0; j < ${#region_zones[@]}; j++)); do
      out+=("${region_zones[$(((j + offset) % ${#region_zones[@]}))]}")
    done
  done
  local IFS=,
  printf '%s' "${out[*]}"
}

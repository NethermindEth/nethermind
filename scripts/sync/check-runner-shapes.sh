#!/usr/bin/env bash
# Passes a sync matrix (JSON array on stdin) through unchanged, failing on entries whose
# machine_type cannot carry local_ssd_count, which startup-script.sh would only catch at boot.
set -euo pipefail

matrix=$(cat)

# -lssd types bundle a fixed number of Local SSDs and take no --local-ssd; plain C3D takes none.
# C3D's standard and highmem -lssd series bundle the same count for a given vCPU size.
errors=$(jq -r '
  {"8": 1, "16": 1, "30": 2, "60": 4, "90": 8, "180": 16, "360": 32} as $bundled_by_vcpus
  | .[]
  | .machine_type as $type
  | .local_ssd_count as $count
  | ($type | capture("^c3d-(standard|highmem)-(?<vcpus>[0-9]+)-lssd$").vcpus // null) as $vcpus
  | ($bundled_by_vcpus[$vcpus // ""]) as $bundled
  | if ($type | test("^c3d-.*-lssd$")) and $bundled == null then
      "\(.network): \($type) is not a C3D -lssd shape this check knows; add its bundled count"
    elif $bundled != null and $bundled != $count then
      "\(.network): \($type) bundles \($bundled) Local SSD(s), but local_ssd_count is \($count)"
    elif ($type | startswith("c3d-")) and ($type | endswith("-lssd") | not) and $count > 0 then
      "\(.network): \($type) cannot attach Local SSD; use a -lssd type or local_ssd_count 0"
    else empty end' <<<"$matrix")

if [ -n "$errors" ]; then
  while IFS= read -r line; do
    echo "::error title=Sync matrix::${line}" >&2
  done <<<"$errors"
  exit 1
fi

printf '%s\n' "$matrix"

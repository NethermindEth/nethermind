#!/usr/bin/env bash
# Passes a sync matrix (JSON array on stdin) through unchanged, failing on entries whose
# machine_type cannot carry local_ssd_count, which startup-script.sh would only catch at boot.
set -euo pipefail

matrix=$(cat)

# -lssd types bundle a fixed number of Local SSDs and take no --local-ssd; plain C3D takes none.
errors=$(jq -r '
  {
    "c3d-standard-8-lssd": 1, "c3d-standard-16-lssd": 1, "c3d-standard-30-lssd": 2,
    "c3d-standard-60-lssd": 4, "c3d-standard-90-lssd": 8, "c3d-standard-180-lssd": 16,
    "c3d-standard-360-lssd": 32
  } as $bundled
  | .[]
  | .machine_type as $type
  | .local_ssd_count as $count
  | if ($type | endswith("-lssd")) and $bundled[$type] != null and $bundled[$type] != $count then
      "\(.network): \($type) bundles \($bundled[$type]) Local SSD(s), but local_ssd_count is \($count)"
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

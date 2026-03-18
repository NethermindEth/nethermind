#!/bin/bash
# Detect hugepages and container memory limits to safely enable GC Large Pages.
# Without this script, large pages are not enabled (removed from runtimeconfig).
set -euo pipefail

# Only configure if the user hasn't already set these
if [[ -z "${DOTNET_GCLargePages:-}" ]]; then
  # Check if hugepages are available on the host
  hugepages_free=0
  if [[ -f /sys/kernel/mm/hugepages/hugepages-2048kB/free_hugepages ]]; then
    hugepages_free=$(cat /sys/kernel/mm/hugepages/hugepages-2048kB/free_hugepages)
  fi

  if [[ "$hugepages_free" -gt 0 ]]; then
    export DOTNET_GCLargePages=1

    # With large pages the GC commits memory upfront, so cap the region range
    # to prevent CLR_E_GC_OOM (0x8013200E) when the runtime over-estimates memory.
    if [[ -z "${DOTNET_GCRegionRange:-}" ]]; then
      max_range=$((32 * 1024 * 1024 * 1024)) # 32 GiB cap

      # Read container memory limit from cgroups
      mem_limit=""
      if [[ -f /sys/fs/cgroup/memory.max ]]; then
        val=$(cat /sys/fs/cgroup/memory.max)
        [[ "$val" != "max" ]] && mem_limit="$val"
      elif [[ -f /sys/fs/cgroup/memory/memory.limit_in_bytes ]]; then
        val=$(cat /sys/fs/cgroup/memory/memory.limit_in_bytes)
        [[ "$val" -lt 9223372036854771712 ]] && mem_limit="$val"
      fi

      if [[ -n "$mem_limit" ]]; then
        pct75=$(( mem_limit * 75 / 100 ))
        # Use the lower of 32 GiB and 75% of container memory
        if [[ "$pct75" -lt "$max_range" ]]; then
          max_range="$pct75"
        fi
      fi

      # Floor: 256 MiB
      min_range=$(( 256 * 1024 * 1024 ))
      [[ "$max_range" -lt "$min_range" ]] && max_range="$min_range"

      export DOTNET_GCRegionRange=$(printf '0x%x' "$max_range")
    fi

    echo "Large pages enabled: hugepages_free=${hugepages_free}, GCRegionRange=${DOTNET_GCRegionRange}"
  fi
fi

exec ./nethermind "$@"

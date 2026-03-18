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
    # Use min(32 GiB, available_memory * 75%, cgroup_limit * 75%).
    if [[ -z "${DOTNET_GCRegionRange:-}" ]]; then
      max_range=$((32 * 1024 * 1024 * 1024)) # 32 GiB cap

      # Read available physical memory from /proc/meminfo (bytes)
      if [[ -f /proc/meminfo ]]; then
        avail_kb=$(awk '/^MemAvailable:/ { print $2 }' /proc/meminfo)
        if [[ -n "$avail_kb" && "$avail_kb" -gt 0 ]]; then
          avail_75=$(( avail_kb * 1024 * 75 / 100 ))
          [[ "$avail_75" -lt "$max_range" ]] && max_range="$avail_75"
        fi
      fi

      # Read container memory limit from cgroups
      cgroup_limit=""
      if [[ -f /sys/fs/cgroup/memory.max ]]; then
        val=$(cat /sys/fs/cgroup/memory.max)
        [[ "$val" != "max" ]] && cgroup_limit="$val"
      elif [[ -f /sys/fs/cgroup/memory/memory.limit_in_bytes ]]; then
        val=$(cat /sys/fs/cgroup/memory/memory.limit_in_bytes)
        [[ "$val" -lt 9223372036854771712 ]] && cgroup_limit="$val"
      fi

      if [[ -n "$cgroup_limit" ]]; then
        cgroup_75=$(( cgroup_limit * 75 / 100 ))
        [[ "$cgroup_75" -lt "$max_range" ]] && max_range="$cgroup_75"
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

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
    # With large pages the GC commits memory upfront (can't lazily commit huge pages),
    # so GCRegionRange becomes actual pinned physical memory, not virtual reservation.
    # Hugepages are UNPAGEABLE — no overcommit, no swap. If we ask for more than
    # physically available, mmap(MAP_HUGETLB) fails and the GC can't initialize.
    #
    # The GC has a known double-commit bug with large pages where it commits ~2x the
    # region range (see https://github.com/dotnet/runtime/issues/103203).
    # Therefore region_range * 2 must fit in available memory.
    #
    # Use 37.5% of available memory (= 75% / 2) so the 2x commit stays within 75%,
    # leaving 25% headroom for native allocations, RocksDB, stack, etc.
    # Final value: min(free_hugepages, available_memory * 37.5%, cgroup_limit * 37.5%).
    min_useful=$((18 * 1024 * 1024 * 1024)) # 18 GiB — minimum useful GC heap for Nethermind (~48 GiB available memory)

    # Start from available hugepages — this is the absolute physical ceiling.
    # Hugepages are unpageable: mmap(MAP_HUGETLB) fails if we exceed this.
    max_range=$(( hugepages_free * 2 * 1024 * 1024 )) # 2 MiB per hugepage

    # Cap to 37.5% of available physical memory
    if [[ -f /proc/meminfo ]]; then
      avail_kb=$(awk '/^MemAvailable:/ { print $2 }' /proc/meminfo)
      if [[ -n "$avail_kb" && "$avail_kb" -gt 0 ]]; then
        avail_pct=$(( avail_kb * 1024 * 375 / 1000 ))
        [[ "$avail_pct" -lt "$max_range" ]] && max_range="$avail_pct"
      fi
    fi

    # Cap to 37.5% of cgroup memory limit (if set)
    cgroup_limit=""
    if [[ -f /sys/fs/cgroup/memory.max ]]; then
      val=$(cat /sys/fs/cgroup/memory.max)
      [[ "$val" != "max" ]] && cgroup_limit="$val"
    elif [[ -f /sys/fs/cgroup/memory/memory.limit_in_bytes ]]; then
      val=$(cat /sys/fs/cgroup/memory/memory.limit_in_bytes)
      [[ "$val" -lt 9223372036854771712 ]] && cgroup_limit="$val" # PAGE_COUNTER_MAX = unlimited
    fi

    if [[ -n "$cgroup_limit" ]]; then
      cgroup_pct=$(( cgroup_limit * 375 / 1000 ))
      [[ "$cgroup_pct" -lt "$max_range" ]] && max_range="$cgroup_pct"
    fi

    if [[ "$max_range" -ge "$min_useful" ]]; then
      export DOTNET_GCLargePages=1
      if [[ -z "${DOTNET_GCRegionRange:-}" ]]; then
        export DOTNET_GCRegionRange=$(printf '0x%x' "$max_range")
      fi
      echo "Large pages enabled: hugepages_free=${hugepages_free}, GCRegionRange=${DOTNET_GCRegionRange}"
    else
      echo "Large pages skipped: effective region range $(( max_range / 1024 / 1024 )) MiB < $(( min_useful / 1024 / 1024 )) MiB minimum"
    fi
  fi
fi

# Enable edge/block PGO if the trimmed profile exists
if [[ -z "${DOTNET_ReadPGOData:-}" ]] && [[ -f "/nethermind/pgo/nethermind.jit" ]]; then
  export DOTNET_ReadPGOData=1
  export DOTNET_PGODataPath=/nethermind/pgo/nethermind.jit
  echo "Edge/block PGO enabled: ${DOTNET_PGODataPath}"
fi

exec ./nethermind "$@"

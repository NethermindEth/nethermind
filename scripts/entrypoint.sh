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
    # so GCRegionRange becomes actual physical memory, not just virtual reservation.
    # Additionally, the GC has a known double-commit bug with large pages where it
    # commits ~2x the region range (see https://github.com/dotnet/runtime/issues/103203).
    # Use 37.5% (half of 75%) to account for the 2x commit, leaving headroom for
    # native allocations, RocksDB, stack, etc.
    # Final value: min(32 GiB, available_memory * 37.5%, cgroup_limit * 37.5%, free_hugepages).
    min_useful=$((4 * 1024 * 1024 * 1024)) # 4 GiB — below this large pages aren't worth the constraints
    max_range=$((32 * 1024 * 1024 * 1024))  # 32 GiB cap

    # Read available physical memory from /proc/meminfo (bytes)
    if [[ -f /proc/meminfo ]]; then
      avail_kb=$(awk '/^MemAvailable:/ { print $2 }' /proc/meminfo)
      if [[ -n "$avail_kb" && "$avail_kb" -gt 0 ]]; then
        avail_pct=$(( avail_kb * 1024 * 375 / 1000 ))
        [[ "$avail_pct" -lt "$max_range" ]] && max_range="$avail_pct"
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
      cgroup_pct=$(( cgroup_limit * 375 / 1000 ))
      [[ "$cgroup_pct" -lt "$max_range" ]] && max_range="$cgroup_pct"
    fi

    # Cap to available hugepages — large pages are unpageable so we can't
    # request more than the host has pre-allocated (mmap MAP_HUGETLB fails)
    hugepages_bytes=$(( hugepages_free * 2 * 1024 * 1024 )) # 2 MiB per hugepage
    [[ "$hugepages_bytes" -lt "$max_range" ]] && max_range="$hugepages_bytes"

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

exec ./nethermind "$@"

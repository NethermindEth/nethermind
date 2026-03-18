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
    # The GC has a known double-commit bug with large pages where it commits ~2x the
    # region range (see https://github.com/dotnet/runtime/issues/103203).
    #
    # Strategy:
    # - Bare metal / no cgroup: use 75% of available memory. The 2x wasted commit is
    #   handled by Linux overcommit (default overcommit_memory=0).
    # - Container with cgroup limit: use 37.5% (half of 75%) because the 2x commit
    #   counts against the cgroup limit and the OOM-killer will enforce it.
    # Final value: min(32 GiB, memory-based cap, free_hugepages).
    min_useful=$((4 * 1024 * 1024 * 1024)) # 4 GiB — below this large pages aren't worth the constraints
    max_range=$((32 * 1024 * 1024 * 1024))  # 32 GiB cap
    has_cgroup_limit=false

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
      has_cgroup_limit=true
      # 37.5%: the 2x commit counts against the cgroup limit / OOM-killer
      cgroup_pct=$(( cgroup_limit * 375 / 1000 ))
      [[ "$cgroup_pct" -lt "$max_range" ]] && max_range="$cgroup_pct"
    fi

    # Read available physical memory from /proc/meminfo (bytes)
    if [[ -f /proc/meminfo ]]; then
      avail_kb=$(awk '/^MemAvailable:/ { print $2 }' /proc/meminfo)
      if [[ -n "$avail_kb" && "$avail_kb" -gt 0 ]]; then
        if [[ "$has_cgroup_limit" == true ]]; then
          # Already capped by cgroup — use 37.5% of available too for consistency
          avail_pct=$(( avail_kb * 1024 * 375 / 1000 ))
        else
          # No cgroup: 75% — Linux overcommit handles the 2x wasted commit
          avail_pct=$(( avail_kb * 1024 * 75 / 100 ))
        fi
        [[ "$avail_pct" -lt "$max_range" ]] && max_range="$avail_pct"
      fi
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

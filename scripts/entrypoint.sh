#!/bin/bash
# Detect container memory limit and configure .NET GC accordingly.
# Ensures large pages (enabled in runtimeconfig) work safely by capping
# the GC region range to the actual available memory.

set -euo pipefail

# Read the container memory limit from cgroups (bytes).
# cgroups v2 uses memory.max; v1 uses memory.limit_in_bytes.
# "max" or missing file means no limit is set.
mem_limit=""
if [[ -f /sys/fs/cgroup/memory.max ]]; then
  val=$(cat /sys/fs/cgroup/memory.max)
  [[ "$val" != "max" ]] && mem_limit="$val"
elif [[ -f /sys/fs/cgroup/memory/memory.limit_in_bytes ]]; then
  val=$(cat /sys/fs/cgroup/memory/memory.limit_in_bytes)
  # v1 uses a very large sentinel (PAGE_COUNTER_MAX) when unlimited
  [[ "$val" -lt 9223372036854771712 ]] && mem_limit="$val"
fi

if [[ -n "$mem_limit" ]]; then
  # Cap GC region range to 75% of the container memory limit.
  # This leaves headroom for native allocations, RocksDB, stack, etc.
  gc_region_range=$(( mem_limit * 75 / 100 ))

  # Floor: 256 MiB — below this the GC can't function well
  min_range=$(( 256 * 1024 * 1024 ))
  [[ "$gc_region_range" -lt "$min_range" ]] && gc_region_range="$min_range"

  # Only set if not already overridden by the user
  if [[ -z "${DOTNET_GCRegionRange:-}" ]]; then
    export DOTNET_GCRegionRange=$(printf '0x%x' "$gc_region_range")
  fi

  echo "Container memory limit: $(( mem_limit / 1024 / 1024 )) MiB, GCRegionRange: ${DOTNET_GCRegionRange}"
fi

exec ./nethermind "$@"

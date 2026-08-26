# shellcheck shell=bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
#
# Shared helpers sourced by the RPC benchmark scripts (start/stop-node, run-flood, etc.).

log() { printf '%s | %s\n' "$(date -u +%H:%M:%S)" "$*"; }
die() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

# Record the machine state a benchmark ran on, so a step change in results can be attributed to
# the host rather than to code. A reboot or kernel upgrade shifts results persistently and is
# otherwise invisible: on 2026-08-13 a 38% step in one payload set was traced to a restart only by
# noticing that /proc/stat's monotonic interrupt counter had gone backwards between two runs.
# boot_id changes on every boot, so it identifies the reboot directly; the rest covers the settings
# that most often move a benchmark across a kernel upgrade.
log_system_provenance() {
  log "=== host provenance ==="
  log "  kernel:      $(uname -r 2>/dev/null || echo unknown)"
  log "  boot_id:     $(cat /proc/sys/kernel/random/boot_id 2>/dev/null || echo unknown)"
  log "  uptime_s:    $(cut -d' ' -f1 /proc/uptime 2>/dev/null || echo unknown)"
  log "  interrupts:  $(awk '/^intr /{print $2}' /proc/stat 2>/dev/null || echo unknown)"
  log "  governor:    $(cat /sys/devices/system/cpu/cpu0/cpufreq/scaling_governor 2>/dev/null || echo n/a)"
  log "  max_freq:    $(cat /sys/devices/system/cpu/cpu0/cpufreq/scaling_max_freq 2>/dev/null || echo n/a) kHz"
  log "  thp:         $(sed -n 's/.*\[\(.*\)\].*/\1/p' /sys/kernel/mm/transparent_hugepage/enabled 2>/dev/null || echo n/a)"
  local vuln
  for vuln in /sys/devices/system/cpu/vulnerabilities/*; do
    [[ -r "$vuln" ]] || continue
    log "  mitigation:  $(basename "$vuln")=$(tr -d '\n' < "$vuln" | cut -c1-60)"
  done
}

# Run a command as root, using sudo only when not already root.
as_root() {
  if [[ "$(id -u)" -eq 0 ]]; then
    "$@"
  else
    sudo "$@"
  fi
}

# perf record must be launched directly as root so its PID is the recorder PID
# persisted for safe teardown, rather than a short-lived sudo wrapper.
require_perf_access() {
  if [[ "$(id -u)" -ne 0 ]]; then
    die "perf profiling requires the self-hosted runner to execute as root; do not wrap perf in sudo because teardown must retain the recorder PID"
  fi
  if ! command -v perf >/dev/null 2>&1; then
    die "perf profiling requires the host perf executable; install the kernel's linux-tools package on this runner"
  fi
  perf_sampling_event \
    || die "perf can sample neither cycles:u nor cpu-clock:u as root; check kernel perf support, kernel.perf_event_paranoid, and perf_event access restrictions"
  echo "perf sampling event: ${PERF_SAMPLING_EVENT}"
}

# Sets PERF_SAMPLING_EVENT to the first event this host can open: hardware cycles where the PMU is
# exposed, otherwise the cpu-clock software timer (cloud ARM VMs typically virtualise no PMU). Both
# attribute CPU time by stack; only the sample source differs.
perf_sampling_event() {
  [[ -n "${PERF_SAMPLING_EVENT:-}" ]] && return 0
  local event
  for event in cycles:u cpu-clock:u; do
    if perf stat --event "$event" -- true >/dev/null 2>&1; then
      PERF_SAMPLING_EVENT="$event"
      return 0
    fi
  done
  return 1
}

# Start the recorder directly and expose its actual PID to the caller through
# PERF_RECORDER_PID. The caller must invoke require_perf_access first.
start_perf_recorder() {
  local frequency="$1" node_pid="$2" output="$3" record_log="$4"
  perf_sampling_event || return 1
  perf record --event "$PERF_SAMPLING_EVENT" --freq "$frequency" --call-graph fp --pid "$node_pid" \
    --output "$output" \
    > "$record_log" 2>&1 &
  PERF_RECORDER_PID=$!
}

# Echo the stable identity fields for a process: start time, comm, and executable.
# RPC_BENCH_PROC_ROOT lets the unit test exercise PID-reuse handling without a live process.
perf_recorder_identity() {
  local pid="$1" proc_root="${RPC_BENCH_PROC_ROOT:-/proc}" stat rest start_time comm executable
  local -a stat_fields
  [[ "$pid" =~ ^[1-9][0-9]*$ ]] || return 1
  [[ -r "$proc_root/$pid/stat" && -r "$proc_root/$pid/comm" ]] || return 1

  stat="$(<"$proc_root/$pid/stat")" || return 1
  rest="${stat##*) }"
  read -r -a stat_fields <<< "$rest"
  start_time="${stat_fields[19]:-}"
  [[ "$start_time" =~ ^[0-9]+$ ]] || return 1
  IFS= read -r comm < "$proc_root/$pid/comm" || return 1
  comm="${comm%$'\r'}"
  executable="$(readlink -f -- "$proc_root/$pid/exe")" || return 1
  [[ -n "$comm" && -n "$executable" ]] || return 1
  printf '%s\t%s\t%s\n' "$start_time" "$comm" "$executable"
}

perf_recorder_matches() {
  local pid="$1" expected_start_time="$2" expected_comm="$3" expected_executable="$4"
  local actual_start_time actual_comm actual_executable
  IFS=$'\t' read -r actual_start_time actual_comm actual_executable < <(perf_recorder_identity "$pid") || return 1
  [[ "$actual_start_time" == "$expected_start_time" \
      && "$actual_comm" == "$expected_comm" \
      && "$actual_executable" == "$expected_executable" ]]
}

# Signal only the recorder process originally started for this benchmark. Returns
# non-zero without sending a signal if PID reuse changed the process identity.
signal_perf_recorder_if_matches() {
  local signal="$1" pid="$2" start_time="$3" comm="$4" executable="$5"
  case "$signal" in
    INT|KILL) ;;
    *) return 2 ;;
  esac
  perf_recorder_matches "$pid" "$start_time" "$comm" "$executable" || return 1
  kill "-$signal" "$pid"
}

# Reject paths unsafe for recursive deletion (absolute, no '..', not '/', >=2 deep).
#   $1 = path, $2 = label for error messages.
assert_sane_dir() {
  local p="$1" label="$2"
  [[ "$p" == /* ]] || die "$label '$p' must be an absolute path"
  [[ "$p" != *..* ]] || die "$label '$p' must not contain '..'"
  local trimmed="${p#/}"; trimmed="${trimmed%/}"
  [[ -n "$trimmed" ]] || die "$label must not be '/'"
  [[ "$trimmed" == */* ]] || die "$label '$p' is too shallow (need at least two path components)"
}

# Canonicalize DB_SOURCE and SCRATCH_ROOT (resolving symlinks so aliased paths can't
# defeat the check), enforce they are disjoint, and re-export the canonical values.
guard_paths() {
  # Precondition: caller verified DB_SOURCE exists. Fail hard rather than silently
  # skip the security invariants below if that precondition is ever violated.
  [[ -d "$DB_SOURCE" ]] || die "guard_paths: DB_SOURCE '$DB_SOURCE' is not a directory (caller must verify it exists first)"
  DB_SOURCE="$(realpath -e -- "$DB_SOURCE")" || die "cannot canonicalize DB_SOURCE"
  SCRATCH_ROOT="$(realpath -m -- "$SCRATCH_ROOT")" || die "cannot canonicalize SCRATCH_ROOT"
  assert_sane_dir "$DB_SOURCE" "DB_SOURCE"
  assert_sane_dir "$SCRATCH_ROOT" "SCRATCH_ROOT"
  case "$DB_SOURCE/" in
    "$SCRATCH_ROOT"/*) die "DB_SOURCE must not be inside SCRATCH_ROOT — scratch is wiped on teardown" ;;
  esac
  case "$SCRATCH_ROOT/" in
    "$DB_SOURCE"/*) die "SCRATCH_ROOT must not be inside DB_SOURCE" ;;
  esac
}

# Fail if anything is still mounted at/below $1 — must precede every recursive
# delete of scratch so an rm -rf never runs through a live overlay/bind mount.
assert_no_mounts_under() {
  local dir mounts
  dir="$(realpath -m -- "$1")"
  mounts="$(awk -v d="$dir" '$2 == d || index($2, d "/") == 1 { print $2 }' /proc/self/mounts 2>/dev/null || true)"
  if [[ -n "$mounts" ]]; then
    die "refusing to delete '$dir' — still mounted: $mounts"
  fi
}

# Remove benchmark containers from ANY run — a hard-interrupted run leaves stale
# containers holding port 8545 and the old overlay mount ns. $@ = name prefixes.
reap_stale_containers() {
  local prefix ids
  for prefix in "$@"; do
    ids="$(docker ps -aq --filter "name=^${prefix}" 2>/dev/null || true)"
    if [[ -n "$ids" ]]; then
      log "Reaping stale container(s) matching '${prefix}*'..."
      # shellcheck disable=SC2086
      docker rm -fv $ids >/dev/null 2>&1 || true
    fi
  done
}

# POST a JSON-RPC request.
#   $1 = url, $2 = JSON body. Echoes the response body.
rpc_post() {
  local url="$1" body="$2"
  curl -sS -m 30 --connect-timeout 5 \
    -H 'Content-Type: application/json' \
    -X POST --data "$body" "$url"
}

# Block until the node answers eth_blockNumber with a non-genesis head, or fail
# (dies early with logs if the container exits). $1=url, $2=timeout s (def 1800), $3=container.
wait_for_rpc() {
  local url="$1" timeout="${2:-1800}" container="${3:-}" elapsed=0 interval=5 resp head head_digits
  log "Waiting for JSON-RPC at $url (timeout ${timeout}s)..."
  while true; do
    if [[ -n "$container" ]] && ! docker ps --format '{{.Names}}' | grep -qx "$container"; then
      log "Container '$container' exited while waiting for RPC. Last 100 log lines:"
      docker logs "$container" 2>&1 | tail -n 100 || true
      die "node container died before serving JSON-RPC"
    fi
    resp="$(rpc_post "$url" '{"jsonrpc":"2.0","method":"eth_blockNumber","params":[],"id":1}' 2>/dev/null || true)"
    head="$(printf '%s' "$resp" | jq -r '.result // empty' 2>/dev/null || true)"
    if [[ "$head" =~ ^0x[0-9a-fA-F]{1,15}$ ]]; then
      head_digits="${head#0x}"
      if [[ "$((16#$head_digits))" -eq 0 ]]; then
        # A snapshot-backed node must report its snapshot head immediately; 0x0
        # means the datadir is wrong/empty and a fresh DB was initialized.
        die "node reports head block 0 — datadir mismatch (snapshot not picked up); refusing to benchmark genesis"
      fi
      log "JSON-RPC is up. Head block: $((16#$head_digits)) ($head)"
      return 0
    fi
    sleep "$interval"
    elapsed=$((elapsed + interval))
    if (( elapsed >= timeout )); then
      die "JSON-RPC did not become ready within ${timeout}s (last response: ${resp:-<none>})"
    fi
    if (( elapsed % 30 == 0 )); then
      log "  still waiting for RPC... (${elapsed}/${timeout}s)"
    fi
  done
}

# Fail unless both nodes report the same eth_blockNumber head — cross-node diffs at
# different heads are meaningless ('latest' differs). $1 = primary url, $2 = reference url.
assert_same_head() {
  local body='{"jsonrpc":"2.0","method":"eth_blockNumber","params":[],"id":1}' a b
  a="$(rpc_post "$1" "$body" 2>/dev/null | jq -r '.result // empty')"
  b="$(rpc_post "$2" "$body" 2>/dev/null | jq -r '.result // empty')"
  [[ -n "$a" && -n "$b" ]] || die "assert_same_head: could not read eth_blockNumber from both nodes ($1 -> '${a:-<none>}', $2 -> '${b:-<none>}')"
  if (( 16#${a#0x} != 16#${b#0x} )); then
    die "head mismatch: $1 is at block $((16#${a#0x})) but $2 is at $((16#${b#0x})) — both nodes must use snapshots taken at the same block"
  fi
  log "Both nodes report head block $((16#${a#0x})) ($a)."
}

# Tamper tripwire over a snapshot dir (recursive listing + sha256 of DB control files);
# baseline-vs-final catches mutation. Listing errors fatal (no partial fingerprint). $1=dir, $2=out.
db_fingerprint() {
  local dir="$1" out="$2" listing
  listing="$(mktemp)"
  if ! find "$dir" \( -type f -o -type d -o -type l \) \
        -printf '%P\t%y\t%s\t%T@\t%m\t%U:%G\t%l\n' > "$listing" 2>"$listing.err"; then
    cat "$listing.err" >&2 || true
    rm -f "$listing" "$listing.err"
    die "db_fingerprint: find failed for '$dir' (see errors above)"
  fi
  if [[ -s "$listing.err" ]]; then
    cat "$listing.err" >&2
    rm -f "$listing" "$listing.err"
    die "db_fingerprint: find reported errors for '$dir' — refusing to produce a partial fingerprint"
  fi
  {
    echo "# rpc-bench fingerprint v2"
    echo "# listing (path<TAB>type<TAB>size<TAB>mtime<TAB>mode<TAB>owner<TAB>linktarget)"
    LC_ALL=C sort < "$listing"
    echo "# control-file-hashes"
    while IFS= read -r -d '' f; do
      sha256sum "$f" | { read -r hash _; printf '%s  %s\n' "$hash" "${f#"$dir"/}"; }
    done < <(find "$dir" -type f \
        \( -name 'CURRENT' -o -name 'IDENTITY' -o -name 'MANIFEST-*' -o -name 'OPTIONS-*' \) \
        -print0 | LC_ALL=C sort -z)
  } > "$out"
  rm -f "$listing" "$listing.err"
}

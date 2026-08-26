# shellcheck shell=bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
#
# Shared helpers sourced by the RPC benchmark scripts.

log() { printf '%s | %s\n' "$(date -u +%H:%M:%S)" "$*"; }
die() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

# Sweep arm label for a client entry with an image: <ctype>_<image tag, non-alphanumerics as _>. Shared with the
# workflow's comment step so both sides derive the same label from the same image ref.
arm_label() { printf '%s_%s' "$1" "$(printf '%s' "${2##*:}" | tr -c 'a-zA-Z0-9' '_')"; }

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

as_root() {
  if [[ "$(id -u)" -eq 0 ]]; then "$@"; else sudo "$@"; fi
}

# Numeric field of a JSON file via jq path; prints the default when absent or non-numeric.
json_number() {
  local file="$1" path="$2" default="${3-0}" value
  [[ -s "$file" ]] || { printf '%s' "$default"; return 0; }
  value="$(jq -r "$path | if type == \"number\" then . else empty end" "$file" 2>/dev/null)"
  printf '%s' "${value:-$default}"
}

need_pyyaml() {
  python3 -c 'import yaml' 2>/dev/null \
    || python3 -m pip install --user pyyaml 2>/dev/null \
    || python3 -m pip install --user --break-system-packages pyyaml \
    || die "PyYAML is required and could not be installed"
}

strip_ansi() { sed -E 's/\x1B\[[0-9;?]*[ -/]*[@-~]//g' "$@"; }

# Absolute, no '..', not '/', at least two components — the shape every recursively deleted path must have.
assert_sane_dir() {
  local p="$1" label="$2"
  [[ "$p" == /* ]] || die "$label '$p' must be an absolute path"
  [[ "$p" != *..* ]] || die "$label '$p' must not contain '..'"
  local trimmed="${p#/}"; trimmed="${trimmed%/}"
  [[ -n "$trimmed" ]] || die "$label must not be '/'"
  [[ "$trimmed" == */* ]] || die "$label '$p' is too shallow (need at least two path components)"
}

# Canonicalize DB_SOURCE and SCRATCH_ROOT and enforce that they are disjoint.
guard_paths() {
  [[ -d "$DB_SOURCE" ]] || die "guard_paths: DB_SOURCE '$DB_SOURCE' is not a directory"
  DB_SOURCE="$(realpath -e -- "$DB_SOURCE")" || die "cannot canonicalize DB_SOURCE"
  SCRATCH_ROOT="$(realpath -m -- "$SCRATCH_ROOT")" || die "cannot canonicalize SCRATCH_ROOT"
  assert_sane_dir "$DB_SOURCE" "DB_SOURCE"
  assert_sane_dir "$SCRATCH_ROOT" "SCRATCH_ROOT"
  case "$DB_SOURCE/" in "$SCRATCH_ROOT"/*) die "DB_SOURCE must not be inside SCRATCH_ROOT" ;; esac
  case "$SCRATCH_ROOT/" in "$DB_SOURCE"/*) die "SCRATCH_ROOT must not be inside DB_SOURCE" ;; esac
}

assert_no_mounts_under() {
  local dir mounts
  dir="$(realpath -m -- "$1")"
  mounts="$(awk -v d="$dir" '$2 == d || index($2, d "/") == 1 { print $2 }' /proc/self/mounts 2>/dev/null || true)"
  [[ -z "$mounts" ]] || die "refusing to delete '$dir' — still mounted: $mounts"
}

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

rpc_post() {
  curl -sS -m 30 --connect-timeout 5 -H 'Content-Type: application/json' -X POST --data "$2" "$1"
}

rpc_head() {
  rpc_post "$1" '{"jsonrpc":"2.0","method":"eth_blockNumber","params":[],"id":1}' 2>/dev/null | jq -r '.result // empty' 2>/dev/null || true
}

# Block until the node serves a non-genesis eth_blockNumber. $1=url, $2=timeout s, $3=container.
wait_for_rpc() {
  local url="$1" timeout="${2:-1800}" container="${3:-}" elapsed=0 interval=5 head
  log "Waiting for JSON-RPC at $url (timeout ${timeout}s)..."
  while true; do
    if [[ -n "$container" ]] && ! docker ps --format '{{.Names}}' | grep -qx "$container"; then
      docker logs "$container" 2>&1 | tail -n 100 || true
      die "node container died before serving JSON-RPC"
    fi
    head="$(rpc_head "$url")"
    if [[ "$head" =~ ^0x[0-9a-fA-F]{1,15}$ ]]; then
      (( 16#${head#0x} > 0 )) || die "node reports head block 0 — datadir mismatch; refusing to benchmark genesis"
      log "JSON-RPC is up. Head block: $((16#${head#0x})) ($head)"
      return 0
    fi
    sleep "$interval"
    elapsed=$((elapsed + interval))
    (( elapsed < timeout )) || die "JSON-RPC did not become ready within ${timeout}s"
    (( elapsed % 30 )) || log "  still waiting for RPC... (${elapsed}/${timeout}s)"
  done
}

assert_same_head() {
  local a b
  a="$(rpc_head "$1")"; b="$(rpc_head "$2")"
  [[ -n "$a" && -n "$b" ]] || die "assert_same_head: could not read eth_blockNumber from both nodes"
  (( 16#${a#0x} == 16#${b#0x} )) || die "head mismatch: $1 is at $((16#${a#0x})) but $2 is at $((16#${b#0x}))"
  log "Both nodes report head block $((16#${a#0x})) ($a)."
}

# Recursive listing plus sha256 of the RocksDB control files; any difference between two fingerprints means the snapshot changed.
db_fingerprint() {
  local dir="$1" out="$2" listing
  listing="$(mktemp)"
  if ! find "$dir" \( -type f -o -type d -o -type l \) -printf '%P\t%y\t%s\t%T@\t%m\t%U:%G\t%l\n' > "$listing" 2>"$listing.err" \
      || [[ -s "$listing.err" ]]; then
    cat "$listing.err" >&2 || true
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
    done < <(find "$dir" -type f \( -name 'CURRENT' -o -name 'IDENTITY' -o -name 'MANIFEST-*' -o -name 'OPTIONS-*' \) -print0 | LC_ALL=C sort -z)
  } > "$out"
  rm -f "$listing" "$listing.err"
}

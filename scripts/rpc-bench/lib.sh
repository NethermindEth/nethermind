# shellcheck shell=bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
#
# Shared helpers for the RPC benchmark scripts.
# Sourced by start-node.sh / stop-node.sh / run-flood.sh / run-ethcallchaos.sh / cleanup.sh.

log() { printf '%s | %s\n' "$(date -u +%H:%M:%S)" "$*"; }
die() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

# Run a command as root, using sudo only when not already root.
as_root() {
  if [[ "$(id -u)" -eq 0 ]]; then
    "$@"
  else
    sudo "$@"
  fi
}

# Reject paths that are unsafe targets for recursive deletion: must be absolute,
# canonical-ish (no '..'), not '/', and at least two components deep.
#   $1 = path, $2 = label for error messages.
assert_sane_dir() {
  local p="$1" label="$2"
  [[ "$p" == /* ]] || die "$label '$p' must be an absolute path"
  [[ "$p" != *..* ]] || die "$label '$p' must not contain '..'"
  local trimmed="${p#/}"; trimmed="${trimmed%/}"
  [[ -n "$trimmed" ]] || die "$label must not be '/'"
  [[ "$trimmed" == */* ]] || die "$label '$p' is too shallow (need at least two path components)"
}

# Canonicalize DB_SOURCE and SCRATCH_ROOT (resolving symlinks so aliased paths
# cannot defeat the check) and enforce that they are disjoint. Re-exports the
# canonical values into the caller's variables.
guard_paths() {
  [[ -d "$DB_SOURCE" ]] || return 0   # caller handles the missing-dir error with better diagnostics
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

# Fail if anything is still mounted at or below the given directory — a guard
# that must precede every recursive delete of scratch, so an rm -rf can never
# run through a live overlay/bind mount.
#   $1 = directory.
assert_no_mounts_under() {
  local dir mounts
  dir="$(realpath -m -- "$1")"
  mounts="$(awk -v d="$dir" '$2 == d || index($2, d "/") == 1 { print $2 }' /proc/self/mounts 2>/dev/null || true)"
  if [[ -n "$mounts" ]]; then
    die "refusing to delete '$dir' — still mounted: $mounts"
  fi
}

# Remove benchmark containers from ANY run (names embed the run id, so a
# hard-interrupted previous run leaves stale containers holding port 8545 and
# the old overlay mount namespace).
#   $@ = container name prefixes.
reap_stale_containers() {
  local prefix ids
  for prefix in "$@"; do
    ids="$(docker ps -aq --filter "name=^${prefix}" 2>/dev/null || true)"
    if [[ -n "$ids" ]]; then
      log "Reaping stale container(s) matching '${prefix}*'..."
      # shellcheck disable=SC2086
      docker rm -f $ids >/dev/null 2>&1 || true
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

# Block until the node answers eth_blockNumber with a non-genesis head, or fail.
# Dies early (with container logs) if the container exits while waiting.
#   $1 = url, $2 = timeout seconds (default 1800), $3 = container name (optional).
wait_for_rpc() {
  local url="$1" timeout="${2:-1800}" container="${3:-}" elapsed=0 interval=5 resp head
  log "Waiting for JSON-RPC at $url (timeout ${timeout}s)..."
  while true; do
    if [[ -n "$container" ]] && ! docker ps --format '{{.Names}}' | grep -qx "$container"; then
      log "Container '$container' exited while waiting for RPC. Last 100 log lines:"
      docker logs "$container" 2>&1 | tail -n 100 || true
      die "node container died before serving JSON-RPC"
    fi
    resp="$(rpc_post "$url" '{"jsonrpc":"2.0","method":"eth_blockNumber","params":[],"id":1}' 2>/dev/null || true)"
    head="$(printf '%s' "$resp" | jq -r '.result // empty' 2>/dev/null || true)"
    if [[ -n "$head" && "$head" != "null" ]]; then
      if [[ "$((16#${head#0x}))" -eq 0 ]]; then
        # A snapshot-backed node must report its snapshot head immediately; 0x0
        # means the datadir is wrong/empty and a fresh DB was initialized.
        die "node reports head block 0 — datadir mismatch (snapshot not picked up); refusing to benchmark genesis"
      fi
      log "JSON-RPC is up. Head block: $((16#${head#0x})) ($head)"
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

# Compute a tamper tripwire over a RocksDB-style directory and write it to a
# file. The fingerprint captures:
#   * a format-version header,
#   * a full recursive listing of files/dirs/symlinks with type, size, mtime,
#     mode, owner and symlink target, and
#   * sha256 of the small control files RocksDB rewrites the instant it opens a
#     DB read-write (CURRENT, IDENTITY, MANIFEST-*, OPTIONS-*).
# Comparing the baseline (pre-run) against the final (post-run) fingerprint
# detects any plausible modification of the pristine snapshot. Hashing is
# limited to the small control files so the check stays fast on a multi-TB DB;
# the residual blind spot (a size- and mtime-preserving in-place rewrite of an
# unhashed SST body) requires deliberate tampering, not a misbehaving node.
# Listing errors are fatal — a partial fingerprint must never masquerade as a
# clean one (or trigger a false tamper alarm at teardown).
#   $1 = directory, $2 = output file.
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

# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
#
# Shared helpers for the RPC benchmark scripts.
# Sourced by start-node.sh / stop-node.sh / run-flood.sh / run-ethcallchaos.sh.

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

# POST a JSON-RPC request.
#   $1 = url, $2 = JSON body. Echoes the response body.
rpc_post() {
  local url="$1" body="$2"
  curl -sS -m 30 --connect-timeout 5 \
    -H 'Content-Type: application/json' \
    -X POST --data "$body" "$url"
}

# Block until the node answers eth_blockNumber, or fail after a timeout.
#   $1 = url, $2 = timeout seconds (default 1800).
wait_for_rpc() {
  local url="$1" timeout="${2:-1800}" elapsed=0 interval=5 resp head
  log "Waiting for JSON-RPC at $url (timeout ${timeout}s)..."
  while true; do
    resp="$(rpc_post "$url" '{"jsonrpc":"2.0","method":"eth_blockNumber","params":[],"id":1}' 2>/dev/null || true)"
    head="$(printf '%s' "$resp" | jq -r '.result // empty' 2>/dev/null || true)"
    if [[ -n "$head" && "$head" != "null" ]]; then
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

# Compute a cheap tamper tripwire over a RocksDB-style directory and write it to
# a file. The fingerprint captures:
#   * a full recursive listing with size + mtime for every file, and
#   * sha256 of the small control files RocksDB rewrites the instant it opens a
#     DB read-write (CURRENT, IDENTITY, MANIFEST-*, OPTIONS-*).
# Comparing the baseline (pre-run) against the final (post-run) fingerprint
# detects ANY modification to the pristine backup. Hashing is limited to the
# small control files so the check stays fast even on a multi-terabyte DB; the
# listing covers everything else via size/mtime.
#   $1 = directory, $2 = output file.
db_fingerprint() {
  local dir="$1" out="$2"
  {
    echo "# listing (path<TAB>size<TAB>mtime)"
    find "$dir" -type f -printf '%P\t%s\t%T@\n' 2>/dev/null | LC_ALL=C sort
    echo "# control-file-hashes"
    find "$dir" -type f \
      \( -name 'CURRENT' -o -name 'IDENTITY' -o -name 'MANIFEST-*' -o -name 'OPTIONS-*' \) \
      -print0 2>/dev/null \
      | LC_ALL=C sort -z \
      | xargs -0 -r sha256sum 2>/dev/null \
      | sed "s#${dir}/##g" \
      || true
  } > "$out"
}

#!/usr/bin/env bash
set -euo pipefail

KIT="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$KIT/../.." && pwd)"
: "${PQ_KEY:?set PQ_KEY to the private key of a premined account}"
: "${CHAIN_ID:?set CHAIN_ID}"
: "${OUT:?set OUT to a fresh output dir}"
PORT="${PORT:-8545}"
FX="${FX:-$ROOT/tools/lean-ffi/target/release/examples/fixtures}"
TO="${TO:-0x000000000000000000000000000000000000dEaD}"
NONCE="${NONCE:-}"
NONCE_KEY="${NONCE_KEY:-$(( $(od -An -tu4 -N4 /dev/urandom) % 4000000000 + 1000 ))}"
RECEIPT_TRIES="${RECEIPT_TRIES:-60}"
REJECT_BAD_PROOF="a wrapper dependency proof failed verification"

die() { echo "FAIL: $*" >&2; exit 1; }
[ ! -e "$OUT" ] || die "$OUT exists; use a fresh OUT"
mkdir -p "$OUT"

(cd "$ROOT/tools/lean-ffi" && cargo build -q --release --locked --example fixtures) > "$OUT/fixtures.build.log" 2>&1 \
  || die "fixtures build failed: $(tail -20 "$OUT/fixtures.build.log")"
dotnet build --disable-build-servers -c release "$KIT/pq-tx.cs" > "$OUT/pq-tx.build.log" 2>&1 \
  || die "pq-tx build failed: $(tail -20 "$OUT/pq-tx.build.log")"
[ -x "$FX" ] || die "$FX is not an executable fixtures signer"
PQTX=(dotnet run -c release --no-build --file "$KIT/pq-tx.cs" --)

call() {
  curl -sf -XPOST -H 'content-type: application/json' "http://127.0.0.1:$PORT" \
    -d "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"$1\",\"params\":[\"$2\"${3:+,\"$3\"}]}" || die "$1 to port $PORT failed"
}

build() {
  "${PQTX[@]}" "$FX" "$CHAIN_ID" - "$2" "$TO" "$3" "$OUT/$1" ${4:-} <<<"$PQ_KEY" > "$OUT/$1.build.log" 2>&1 \
    || die "pq-tx $1 failed: $(cat "$OUT/$1.build.log")"
  [ -s "$OUT/$1.wrapper.hex" ] && [ -s "$OUT/$1.corrupt.hex" ] && [ -s "$OUT/$1.txhash" ] || die "pq-tx $1 wrote no wrapper, corrupt wrapper or txhash"
}

send() {
  call eth_sendProofWrapper "$(cat "$OUT/$1")" > "$OUT/$1.resp"
}

expect_error() {
  local got
  send "$1"
  got=$(jq -r '.error.message // empty' "$OUT/$1.resp")
  [ "$got" = "$2" ] || die "$1: want error '$2', got $(cat "$OUT/$1.resp")"
  echo "ok $1 rejected: $2"
}

expect_included() {
  local want got r status block
  want=$(cat "$OUT/$1.txhash")
  send "$1.wrapper.hex"
  got=$(jq -r '.result[0] // empty' "$OUT/$1.wrapper.hex.resp")
  [ "$got" = "$want" ] || die "$1: want accepted $want, got $(cat "$OUT/$1.wrapper.hex.resp")"
  for _ in $(seq 1 "$RECEIPT_TRIES"); do
    r=$(call eth_getTransactionReceipt "$want")
    if [ "$(jq -r .result <<<"$r")" != null ]; then
      printf '%s\n' "$r" > "$OUT/$1.receipt"
      status=$(jq -r .result.status <<<"$r")
      block=$(jq -r .result.blockNumber <<<"$r")
      [ "$status" = 0x1 ] || die "$1: receipt status $status in block $block"
      echo "ok $1 $want block $block status 0x1"
      BLOCK=$(( block ))
      return 0
    fi
    sleep 2
  done
  die "$1: no receipt for $want after $RECEIPT_TRIES tries"
}

if [ -z "$NONCE" ]; then
  build sender 0 123
  NONCE=$(jq -r '.result // empty' <<<"$(call eth_getTransactionCount "$(cat "$OUT/sender.sender")" pending)")
  [[ "$NONCE" =~ ^0x[0-9a-fA-F]+$ ]] || die "eth_getTransactionCount for $(cat "$OUT/sender.sender") returned '$NONCE'"
  NONCE=$(( NONCE ))
fi
[[ "$NONCE" =~ ^(0|[1-9][0-9]*)$ && "$NONCE_KEY" =~ ^[1-9][0-9]*$ ]] || die "NONCE must be a decimal integer and NONCE_KEY a positive one"
echo "nonce $NONCE nonce-key $NONCE_KEY receipt-tries $RECEIPT_TRIES"
build tx0 "$NONCE" 123
expect_error tx0.corrupt.hex "$REJECT_BAD_PROOF"
expect_included tx0; b0=$BLOCK

build key1 0 123 "$NONCE_KEY"
expect_error key1.corrupt.hex "$REJECT_BAD_PROOF"
build key1early 1 125 "$NONCE_KEY"
expect_error key1early.wrapper.hex "keyed nonce sequence not current"
expect_included key1; b1=$BLOCK

build key1n1 1 123 "$NONCE_KEY"
expect_included key1n1; b2=$BLOCK

build key1replay 0 123 "$NONCE_KEY"
cmp -s "$OUT/key1replay.wrapper.hex" "$OUT/key1.wrapper.hex" || die "key1replay is not byte-identical to key1"
expect_error key1replay.wrapper.hex "already known"

build key1replay124 0 124 "$NONCE_KEY"
expect_error key1replay124.wrapper.hex "keyed nonce sequence not current"

echo "PASS pq: blocks $b0 $b1 $b2"

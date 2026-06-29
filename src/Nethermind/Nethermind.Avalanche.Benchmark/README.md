# Nethermind.Avalanche.Benchmark

A standalone console harness that measures **Nethermind's EVM block-execution throughput on Avalanche
C-Chain blocks**. It is the Nethermind side of a "is Nethermind faster than Coreth at executing C-Chain
blocks?" comparison; Coreth's rate is read separately from AvalancheGo's bootstrap logs (see
[Comparing against Coreth](#comparing-against-coreth)).

The harness loads a contiguous range of real C-Chain blocks (standard Ethereum block RLP), executes each
block's transactions through Nethermind's production `BranchProcessor` pipeline under **Avalanche spec
rules** (chain id 43114), and reports aggregate throughput plus per-block percentiles.

## What it measures (and what it deliberately ignores)

- **Measures**: wall-clock time to execute each block's transactions and apply the resulting state
  changes — EVM execution, state reads/writes, receipts, gas accounting — through the same
  `BranchProcessor` the live client uses, wired via Nethermind's test DI modules with the Avalanche
  `ISpecProvider` overriding `ISpecProvider`.
- **Ignores (by design)**:
  - **Avalanche `extData` / atomic transactions.** The harness consumes *standard Ethereum block RLP*
    (header ‖ transactions ‖ uncles ‖ withdrawals). The C-Chain's atomic (import/export) transactions and
    the `extData` payload carry no EVM transactions and do not affect the EVM execution being measured, so
    they are not loaded or replayed. This is the documented simplification: the throughput figure is a
    pure EVM-execution number, not a full C-Chain block-acceptance number.
  - **Block / header / state-root validation.** Blocks are processed with
    `ProcessingOptions.NoValidation`, so consensus checks (seal, base-fee, state-root match) are skipped.
    This keeps the measurement focused on execution and avoids needing the full canonical trie.
  - **Avalanche 5-field account RLP / storage-key transform.** Those affect the *persisted* state-trie
    encoding (state-root parity with Coreth), not EVM execution latency. The harness runs over an
    in-memory state and does not assert state-root parity.

## Building

```bash
dotnet build src/Nethermind/Nethermind.Avalanche.Benchmark/Nethermind.Avalanche.Benchmark.csproj -c release
```

The project targets `net10.0` and is **not** part of any solution (`Nethermind.slnx`, `Benchmarks.slnx`);
build it directly by path.

## Running

```bash
dotnet run --project src/Nethermind/Nethermind.Avalanche.Benchmark -c release -- \
  --blocks   /path/to/blocks \
  --prestate /path/to/prestate.json \
  --warmup   1 \
  --out      /path/to/per-block.csv
```

| Flag          | Required | Meaning |
|---------------|----------|---------|
| `--blocks`    | yes      | A directory of `*.rlp` files (one block per file) **or** a single file with block RLPs concatenated back-to-back. Files may hold raw binary RLP or a single `0x`-prefixed hex string. |
| `--prestate`  | no       | JSON pre-state to seed before execution (see [Pre-state](#pre-state)). Without it the run starts from an empty state. |
| `--chainspec` | no       | Avalanche chainspec JSON. Defaults to `avalanche-cchain.json` shipped next to the executable. |
| `--warmup`    | no       | Number of unmeasured warmup rounds over the range (default `0`) to amortize JIT and cold caches. |
| `--out`       | no       | Write a per-block CSV (`block_number,gas_used,tx_count,exec_ms,succeeded,error`). |

Exit codes: `0` all blocks succeeded, `3` some blocks failed (still reports throughput over the
successful ones), `1` bad arguments, `2` fatal error.

### Output

```
===== Avalanche C-Chain block execution =====
Blocks executed:   1000/1000
Total transactions:          84,213
Total gas:          12,034,221,901
Total exec time:       3,142.881 ms
---------------------------------------------
Throughput:             3,829.41 Mgas/s
Block rate:               318.18 blocks/s
---- per-block execution time (ms) ----------
  mean: 3.143   min: 0.041   max: 41.882
  p50:  2.110   p90: 7.430   p99: 18.221
=============================================
```

## Exporting real C-Chain blocks from a synced node

The harness expects **standard Ethereum block RLP**. The simplest export uses Nethermind's / geth's
`debug_getRawBlock`, which returns the canonical RLP directly. Against an AvalancheGo + Coreth node, the
C-Chain JSON-RPC endpoint is `/ext/bc/C/rpc`:

```bash
# One block, as 0x-prefixed RLP hex, saved to a per-block file:
N=43000000
curl -s -X POST http://127.0.0.1:9650/ext/bc/C/rpc \
  -H 'content-type: application/json' \
  --data "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"debug_getRawBlock\",\"params\":[\"$(printf '0x%x' $N)\"]}" \
  | jq -r '.result' > blocks/$N.rlp
```

Loop over a contiguous range to fill a `blocks/` directory (the harness sorts by block number and
verifies contiguity):

```bash
mkdir -p blocks
for N in $(seq 43000000 43000999); do
  HEX=$(printf '0x%x' "$N")
  curl -s -X POST http://127.0.0.1:9650/ext/bc/C/rpc \
    -H 'content-type: application/json' \
    --data "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"debug_getRawBlock\",\"params\":[\"$HEX\"]}" \
    | jq -r '.result' > "blocks/$N.rlp"
done
```

If `debug_getRawBlock` is not enabled, you can instead fetch full blocks with
`eth_getBlockByNumber(<hex>, true)` and re-encode them to RLP — but `debug_getRawBlock` is preferred
because it yields the exact canonical bytes with no re-encoding ambiguity.

> Note: Coreth's `debug_getRawBlock` returns the C-Chain block in its Coreth wire form. The EVM-relevant
> fields (header, transactions) match Ethereum RLP; the harness decodes those and ignores any trailing
> `extData` via `RlpBehaviors.AllowExtraBytes`. If your export tool wraps the block differently, prefer a
> tool that emits plain Ethereum block RLP.

### Pre-state

To execute blocks at height `N..M`, the state read by those transactions (sender balances/nonces,
called-contract code, touched storage) must exist. Provide it as a flat `address -> account` JSON map
(the same shape as a geth state dump / EF-test "pre" section):

```json
{
  "0x1111111111111111111111111111111111111111": {
    "balance": "0x21e19e0c9bab2400000",
    "nonce":   "0x5",
    "code":    "0x6080...",
    "storage": { "0x00": "0x01", "0x01": "0xdeadbeef" }
  }
}
```

All fields are optional and accept `0x`-prefixed hex. The most reliable way to obtain a correct pre-state
is to dump the canonical state at block `N-1` from a synced node (e.g. `debug_dumpBlock`, or an
account/storage export keyed on the addresses your range touches). Without a complete pre-state, blocks
whose transactions read un-seeded accounts will surface as **failed blocks** (reported, not fatal); the
throughput figure is then computed over the blocks that did execute.

## Comparing against Coreth

Coreth's execution rate is observed from **AvalancheGo's bootstrap logs**, where the C-Chain VM logs its
block-execution progress while replaying historical blocks. The relevant line is the periodic
`executing blocks` message:

```
INFO <C Chain> ... executing blocks {"numExecuted": 200000, "numToExecute": 1234567, "eta": "12m3s"}
```

To derive Coreth's blocks/s, take two `executing blocks` lines and divide the delta in `numExecuted` by
the wall-clock delta between their timestamps:

```bash
# Pull (timestamp, numExecuted) pairs from the node log and compute blocks/s between consecutive samples.
grep "executing blocks" avalanchego.log \
  | sed -E 's/.*"numExecuted": *([0-9]+).*/\1/' \
  > executed.txt
# Pair each numExecuted with its log timestamp, then: (Δ numExecuted) / (Δ seconds) = Coreth blocks/s.
```

Compare on a like-for-like basis:

- Use the **same contiguous block range** for both sides.
- Coreth's bootstrap "executing blocks" includes full block acceptance (execution + state commit +
  verification), so it is a *broader* metric than this harness's `NoValidation` EVM-execution number. When
  reporting, state this difference explicitly: Nethermind's number is pure EVM execution; Coreth's
  bootstrap number includes acceptance overhead. For the closest comparison, prefer Coreth's per-block
  execution timing if available, or annotate the acceptance-overhead gap.
- Both `blocks/s` and `Mgas/s` are reported here; `Mgas/s` is the more robust cross-client metric because
  it normalizes for block fullness.
```

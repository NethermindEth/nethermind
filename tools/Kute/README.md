# Kute

Kute - is a benchmarking tool developed at Nethermind to simulate an Ethereum Consensus Layer, expected to be used together with the Nethermind Client. The tool sends JSON-RPC messages to the Client and measures its performance.

## Prerequisites

This is a C# project and as such, it requires the [dotnet 9](https://dotnet.microsoft.com/en-us/download) SDK. Once installed, just run:

```bash
dotnet build [-c Release]
```

## Get JSON-RPC messages

To get real JSON-RPC messages, run the Nethermind Client using the `RpcRecorderState` state feature flag (see [JsonRpc module](https://docs.nethermind.io/nethermind/ethereum-client/configuration/jsonrpc)). The minimum required value is `Request` (`All` is also valid); this usually involves adding `--JsonRpc.RpcRecorderState <Request|All>` to your execution flags.

## Run

> We'll assume that the JWT secret used by the Nethermind Client is stored in `/keystore/jwt-secret`.

Kute includes a built in help that can be accessed by the options `-h | --help`.

Some typical usages are as follows:

### Connect to a Nethermind Client running at a specific address using a single file

```bash
-a http://localhost:8551 -s /keystore/jwt-secret -i /rpc.0
```

### Use all messages in the directory `/rpc-logs`

```bash
-a http://localhost:8551 -s /keystore/jwt-secret -i /rpc-logs
```

### Use a single messages file and emit results as HTML

```bash
-a http://localhost:8551 -s /keystore/jwt-secret -i /rpc.0 -o Json
```

### Use a single message file and emit results as JSON, while reporting metrics to a Prometheus Push Gateway (*)

```bash
-a http://localhost:8551 -s /keystore/jwt-secret -i /rpc.0 -o Json -g http://localhost:9091
```

### Use a single message file and report to a Prometheus Push Gateway with additional metrics labels

```bash
-a http://localhost:8551 -s /keystore/jwt-secret -i /rpc.0 -g http://localhost:9091 -l key1=value1,key2=value2 -l key3=value3
```

### Use a single message file and report to a Prometheus Push Gateway with basic auth

```bash
-a http://localhost:8551 -s /keystore/jwt-secret -i /rpc.0 -g http://localhost:9091 --gateway-user user --gateway-pass pass
```

### Use a single messages file and record all responses into a new file

```bash
-a http://localhost:8551 -s /keystore/jwt-secret -i /rpc.0 -r rpc.responses.txt
```

### Use a single message file, using only `engine` and `eth` methods

```bash
-a http://localhost:8551 -s /keystore/jwt-secret -i /rpc.0 -f engine,eth
```

### Use a single message file, using only the first 100 methods

```bash
-a http://localhost:8551 -s /keystore/jwt-secret -i /rpc.0 -f .*=100
```

### Use a single message file, using only the first 50 `engine_newPayloadV2` or `engine_newPayloadV3` methods

```bash
-a http://localhost:8551 -s /keystore/jwt-secret -i /rpc.0 -f engine_newPayloadV[23]=50
```

## Replay a state-reading trace: `kute replay`

The root command drives the Engine API. The `replay` subcommand targets the other side of the node:
a captured trace of state-reading calls (`eth_call`, `eth_getBalance`, `trace_*`, ...) replayed
against the unauthenticated JSON-RPC port, with the block parameter forced to the node's current head
and the load stepped through a range of concurrency levels.

It exists because a trace recorded days ago names blocks a parked node no longer has. Rewriting every
request's block parameter to `latest` makes the trace replayable against any head.

Two things in a capture go stale at a different height, and both are fixed by default:

- **The block parameter** names a block the node may have pruned.
- **Fee fields** (`gasPrice`, `maxFeePerGas`, `maxPriorityFeePerGas`, `maxFeePerBlobGas`) were
  priced against the base fee at capture time. Nethermind enforces the base fee on a priced
  `eth_call`, so once the network's base fee rises above a captured fee, that call is rejected before
  it executes. The rejected share drifts with the network, and a rejected call returns in
  microseconds, so the run silently gets *faster* as the base fee climbs. Stripping trades that drift
  for a fixed, smaller distortion: the call executes with an effective gas price of zero, so a
  contract that branches on `GASPRICE` can take a different path (`BASEFEE` is unaffected). Only the
  top-level call object is stripped; transactions nested inside an `eth_simulateV1` payload keep
  their captured fees. Pass `--keep-fees` to replay them as captured.

Note that stripping is not the same transformation as repricing. If a corpus has to execute identically
on a client that skips fee checks, replay the same pre-transformed file on both rather than relying on
either client's default handling.

```bash
kute replay -i capture.jsonl.zst -a http://localhost:8545 -c 1-32 -n 2000 -w 200 -p
```

Reads `.jsonl`, `.jsonl.gz` and `.jsonl.zst`. No JWT secret is needed for port 8545; pass `-s` only
when replaying against an authenticated endpoint.

The block parameter's position is per-method, so only methods with a known position are rewritten:
`eth_call`, `eth_estimateGas`, `eth_createAccessList`, `eth_simulateV1`, `eth_getBalance`,
`eth_getCode` and `eth_getTransactionCount` (second parameter); `eth_getStorageAt`, `eth_getProof` and
`trace_call` (third); `eth_getBlockByNumber` (first). Any other method is replayed untouched rather
than guessed at, since rewriting the wrong slot would corrupt the request and leave the stale block.

A record that is itself a JSON-RPC batch cannot be retagged or stripped, so a run that would have to
edit one fails on it instead of silently sending it as captured; `-b keep --keep-fees` replays
batches verbatim.

### How a level is measured

Each concurrency level runs twice over the same prefix of the trace: a warm-up pass whose latencies
are discarded, then the measured pass. Both share one connection pool, so the measured window never
pays connection setup. Exactly `-c` requests are kept in flight by that many persistent workers, so
the level label is the load actually offered.

Connections are opened by a priming burst before the warm-up pass: one request per connection, each
held until the whole burst is serializing, because the pool only opens a fresh connection while every
existing one is busy. Priming and warm-up failures are reported even without `-p`, since a failed
warm-up leaves the measured window cold. `-w 0` opts out of priming and warming altogether.

Every level replays the same records, which is what makes levels comparable. Size a level with
`-n <count>` (measured requests) or `-d <seconds>` (wall-clock cap), and note that at low concurrency
a whole 50k-record trace takes a long time: `-n 0` replays all of it.

The duration cap is timed from the level's first request, so decompressing a `--skip` prefix does not
consume it, and it gates sending rather than reading: once it expires, requests the reader had already
queued are dropped instead of sent.

A run that sends nothing - an empty trace, or `--skip` past its end - exits non-zero rather than
reporting an empty success.

### Options that matter

| Option | Purpose |
|---|---|
| `-c, --concurrency` | `1-32` doubles (1, 2, 4, ... 32); `1,4,12` is an explicit list; `8` is one level |
| `-b, --block` | Block parameter forced on every request; `keep` replays the captured one |
| `-n, --requests` | Measured requests per level; `0` replays the whole trace |
| `-w, --warmup` | Requests sent and discarded before each measured window |
| `-d, --duration` | Stop a level once its measured window reaches this many seconds, timed from its first request |
| `--skip` | Records skipped at the start of the trace |
| `-o, --output` | `Pretty`, `Json` or `Csv`; `--output-file` writes it to disk |
| `--max-failure-rate` | Percentage of failed requests above which the run exits non-zero (default 1) |
| `--keep-fees` | Replay captured fee fields instead of stripping them |
| `--dry-run` | Stream and rewrite without sending, verifying every edit was applied |

### Validate a capture before using it

`--dry-run` decompresses the whole trace, applies every edit and fails loudly on any record left
needing one -- a missing block parameter, or a fee field that survived. It needs no node, and it
reports how fast the harness alone can push the trace, which is the ceiling any measurement sits
under. The `retagged` and `de-feed` counts say how many records each edit touched.

```bash
kute replay -i capture.jsonl.zst --dry-run -n 0 -p
```

### Reading the report

`rps` counts completed requests, failures included, since a failed request still consumed node time.
`failed` is split by kind in a line under the table: a JSON-RPC `error` member, a non-success HTTP
status, and a transport error or timeout are three different problems. Percentiles are nearest-rank;
`p99` needs a few thousand samples before it means anything.

### Why this path avoids the parsed-document pipeline

Captured `eth_call` records carrying state overrides run to hundreds of kilobytes each, most of it
override bytecode. Parsing each record into a document and writing it back out would cost more than
the node spends answering it, and the harness would become what the numbers measure. Instead records
are moved as UTF-8 bytes, and locating the block parameter stops the scan before the override map,
which is the bulk of the record. Every record is still copied once into a pooled buffer, because the
reader's buffer is reused; what a record needing no change avoids is being re-encoded.

### Prometheus Push Gateway

Since Kute is not a long-lived application it's unreasonable for Prometheus or similar tools to scrape for metrics. Instead, Kute leverages [Prometheus Push Gateway](https://github.com/prometheus/pushgateway), a service that is intended to be used for ephemeral and batch jobs. Once Kute finishes processing all requests, it will report the metrics to the Gateway, which later will be scraped by Prometheus or similar tools.

### TODO

There are some features that we might add in the future, if they end up being required:

- Validate the responses from the Nethermind Client (a "pedantic" mode)
- Other report outputs for the root command (`replay` already emits CSV)

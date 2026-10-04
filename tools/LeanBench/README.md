# Lean proof and devp2p benchmarks

[Daisugi load, aggregation and mobile graphs](results/2026-10-03/daisugi/README.md) retain the measured source revision, binary hashes and raw samples. The [earlier baseline](results/2026-10-02/README.md) remains available with its original provenance.

This standalone tool measures the pinned native leanVM/SPHINCS backend and production RLPx Snappy/AES/MAC codecs over localhost TCP. It uses a real production transaction pool through the test node's DI setup. Session secrets are preset; capability exchange, Internet latency and TCP/IP headers are outside the wire-byte measurement.

Current builds use the mixed-recursion profile at temporary LeanVM fork commit `854997bd156f47f1b1ce2192c4499741f29bd0df`: ABI 5, guest key `9370d760abb55fdf02acc7e8d40688c425815c3d25a2aea3c030b2ae1ab51ace`, Daisugi/Keccak signatures and one recursive proof for both schemes. The profile accepts 256 dependencies, 16 generic claims, 2048 instructions and inverse-rate log 1. Earlier captures retain their original profiles and binary hashes; they do not measure this guest. Large signature batches use small native leaves and recursive combinations inside the timed proving call, including the entire tree's work. Raw generic proving requires substantial memory; use the bounded, preproved [fresh-devnet plan](devnet/consensus/README.md) before live testing.

Requires .NET 10 and Rust 1.99. Run from the repository root:

```sh
cargo run --manifest-path tools/lean-ffi/Cargo.toml --release --locked --example bench_fixtures -- /tmp/lean-bench-vectors 2048 1200
dotnet run --project tools/LeanBench -c Release -p:BuildLeanFfi=true -- --fixtures=/tmp/lean-bench-vectors --out=/tmp/lean-bench-results
python3 tools/LeanBench/plot.py /tmp/lean-bench-results/results.json --output=/tmp/lean-bench-results/plots
```

The fixture generator cycles sixteen deterministic SPHINCS keys and signs distinct messages containing fixture identity and nonce. Generic STARK proofs have distinct public inputs and pinned program commitments. Generation is outside timed rows and its cost is saved separately (per-proof CSV and overall generation metadata). Every timed crypto/load batch actually proves fresh distinct claims. Increasing duration/rates can require more fixtures; exhaustion fails the run.

- `crypto`: sequential proving and verification of 1/4/8/16 signatures, one generic STARK, mixed claims, and block aggregates of 64/128 signatures. Default three warmups and five measured batches.
- `load`: open-loop batch arrivals through a bounded FIFO, native proving, localhost transport, proof verification and actual pool admission. Default ten seconds at 1/10/25/50/100 offered transactions per second. Full queues drop whole batches. Shared-signature transactions are labeled separately.
- `protocol-preproved`: distinct proofs prepared before timing, then delivered as 64KiB chunks over encrypted localhost TCP through the actual lean/1 receive handler and production BackgroundTaskScheduler, with a decorator that tracks drain without changing capacity, deadlines or block-processing cancellation. Measures reassembly and its one-active/one-pending queue, including local ingress byte/object limits, pending-wrapper drops and scheduler capacity/deadline cancellation. Unverified batches use a combined reason because those causes are not attributed individually. Preparation time is reported. Preparation is bounded to 4096 objects and 512 MiB of retained wrapper bytes (plus one bounded encoding/native work allocation); larger runs fail with an explicit bound error before starting another proof. Pool admission events capture accepted hashes and consume accepted entries without executing them. Historical October 2 rows used a whole-wrapper format; October 3 chunk captures recorded the then-current `lean/2` name. Current chunked builds use `lean/1`. Archived metadata and graphs retain their measured format/version.
- `transport`: random incompressible objects of 1KiB/1MiB/10MiB over the same encrypted TCP pipeline. Default 1/10/25/50 offered objects per second. Objects above the shared 10MiB wrapper bound are rejected.

Transactions use a secp256k1 validation prefix and real Lean dependency claims; the load rows measure proof-bearing admission. Accepted pool entries are removed so funding, nonce gaps and pool capacity do not dominate sustained admission. Chain state remains fixed; this measures admission rather than transaction execution. Every independent case/load starts with a fresh node. Useful transaction goodput counts unique admitted transaction RLP bytes, payload throughput includes proofs/wrappers, and wire throughput counts encrypted RLPx bytes. Latency runs from scheduled arrival through admission and includes queueing; new-run percentiles use nearest-rank `sorted[ceil(p * count) - 1]`; protocol percentiles include only accepted batches and end at the last admitted transaction in each batch; duration includes drain. At low loads batch quantization makes actual offered load differ from the requested rate.

`results.json`, `results.csv`, and per-batch `samples.csv` retain counts, proof sizes, goodput, payload/wire throughput, drops/rejections, latency and CPU. `processCpuMs / (durationSeconds * 1000)` gives average CPU cores used. Load-stage process CPU intervals overlap and must not be summed; sequential crypto stage measurements can be compared directly.

For a short smoke run:

```sh
dotnet run --project tools/LeanBench -c Release -p:BuildLeanFfi=true -- --fixtures=/tmp/lean-bench-vectors --out=/tmp/lean-bench-smoke --seconds=1 --warmups=1 --repetitions=1 --rates=10 --cases=sphincs1,sphincs16,stark1,mixed16 --protocol-rates=10 --object-rates=1 --object-sizes=10485760
```

Controls use `--name=value`: `seconds`, `queue`, `warmups`, `repetitions`, `rates`, `cases`, `block-counts` (1–256), `protocol-cases`, `protocol-rates`, `object-rates`, and `object-sizes`. Empty case lists disable that group. The loopback result is a local CPU/codec/admission baseline, not a prediction of WAN throughput.

## Chunked mixed traffic

[Measured chunk comparison and mobile plots](results/2026-10-03/mixed/README.md).

```sh
dotnet run --project tools/LeanBench -c Release -p:BuildLeanFfi=true -- --mixed-traffic=true --fixtures=/tmp/lean-bench-vectors --out=/tmp/lean-mixed --object-sizes=1048576,10485760 --chunks=0,32768,65536,131072 --wire-mbps=32 --probe-ms=20 --repetitions=3
python3 tools/LeanBench/plot.py /tmp/lean-mixed/results.json --output=/tmp/lean-mixed/plots
```

This mode explicitly enables the prototype bulk path on production `PacketSender`, then uses the chunk serializer and Snappy/AES/MAC codecs over real localhost TCP. Whole-wrapper writes and each chunk await completion of the actual socket write. Concurrent probes cycle serialized `eth/GetBlockHeaders`, single-header `eth/BlockHeaders`, and `p2p/Ping` messages. Chunk size zero selects the whole-wrapper baseline. Unlike the original transport rows, this path passes through `PacketSender`.

The terminal socket writer paces encrypted bytes in 4KiB quanta. `wire-mbps` is a simulated application bandwidth cap, with no kernel network shaping, packet loss or WAN RTT. Probe latency measures scheduled request-to-receiver delivery, not a response round trip. The row timeout scales with uncompressed transfer size, repetitions and the wire cap, with a five-minute minimum; `--row-timeout-seconds` overrides it within a one-day limit. Rows report p50/p95/maximum control latency, object goodput, encrypted bytes including controls, whole-process CPU and drops. Raw object/control samples and compiled binary hashes are saved.

SPHINCS wrappers with one/sixteen distinct claims and a single generic STARK wrapper are natively proved and verified before transport timing. `block-stark16` transports a real raw block-proof envelope; it is not a mempool wrapper, whose generic dependency limit is one. Repeated delivery measures transport and scheduling only; it makes no admission or crypto throughput claim. Preparation timings are single observations. Set `--proof-cases=` to run synthetic objects without native fixtures. Sender queue overflow fails the run rather than presenting missing controls as successful delivery.

## Archived measurements

The committed October captures used a `Task.Run` scheduler stand-in for `protocol-preproved`.
They exercised the production handler, reassembly and pool, but did not measure the production
scheduler's capacity, timeout or block-processing pause/cancellation policies. Their fixed-chain
admission results are not production scheduler capacity estimates. Current runs use the real
scheduler; archived metadata, measurements and graphs retain the original capture unchanged.

Archived full/load/protocol latency summaries use `sorted[ceil((count - 1) * p)]`, which can
select the maximum for small samples. Archived mixed rows already use nearest-rank; current
runs use nearest-rank uniformly. With fewer than twenty samples, nearest-rank p95 is the maximum; these short captures do not estimate a stable tail distribution. Graphs plot the stored percentiles rather than recomputing or
silently changing historical measurements.

The earlier carried-generic profile selected the shortest retained witness for duplicate
claims. Its [exploratory duplicate capture](results/2026-10-03/generic-duplicates/README.md)
measures that historical policy; it does not describe the current single mixed-root profile.

Large captured JSON files are stored as deterministic `.json.gz`; decompression preserves every
byte of their original metadata and samples. [Archive checksums](results/archive-integrity.json)
record both compressed and original SHA-256 values. CSVs, provenance and mobile PNGs remain
readable. Generated SVGs are omitted from version control; regenerate them with:

```sh
python3 tools/LeanBench/plot.py tools/LeanBench/results/2026-10-03/daisugi/full/results.json.gz --svg --output=/tmp/lean-daisugi-plots
```

The Daisugi full and mixed captures share the identical [fixture timing CSV](results/2026-10-03/daisugi/fixture-generation.csv).
Packaging changes do not change the captured source revisions or measured values.

## Historical duplicate generic-witness normalization

[Measured samples, recorded source and reproduction scope](results/2026-10-03/generic-duplicates/README.md)
remain unchanged. Reproduce them only from their recorded source revision. The current
mixed-root profile carries no generic witness trailer and accepts inverse-rate log 1 only;
the old rate-1/rate-2 shortest-witness benchmark and fixture mode are removed. Requests
for `--duplicate-normalization` or `--duplicate-generic` fail explicitly.

## Native prover sizing

Build once, then measure the example directly in a fresh process; running it through Cargo would include compiler memory. This external Python `resource.getrusage(RUSAGE_CHILDREN)` measurement reports bytes on macOS and KiB converted to bytes on Linux:

```sh
(cd tools/lean-ffi && RUSTFLAGS='-C target-cpu=native' cargo build --release --locked --example resource_probe)
python3 - 4 direct <<'PY'
import resource, subprocess, sys
result = subprocess.run(["tools/lean-ffi/target/release/examples/resource_probe", *sys.argv[1:]], check=True, capture_output=True, text=True)
usage = resource.getrusage(resource.RUSAGE_CHILDREN)
print(result.stdout, end="")
print("peak_rss_bytes", usage.ru_maxrss * (1 if sys.platform == "darwin" else 1024))
print("process_cpu_seconds", usage.ru_utime + usage.ru_stime)
PY
```

Use `16 raw-binary-tree` for the current production leaf policy. `proving_ms` includes every leaf and recursive combination in this mode. `direct` proves one raw batch (at most eight claims). `children` prepares singleton proofs before measuring their recursive combination: its `child_preparation_ms` is separate, so its `proving_ms` cannot be compared to end-to-end raw-tree proving. All modes report fixture-generation and total times; process peak RSS includes fixture generation and child preparation. These probes are separate from the archived load/crypto datasets and establish no hard memory bound.

## Functional devnets

The [two-Runner guide](devnet/README.md) adds signed-wrapper generation, a Python Engine driver,
canonical-hash proof mutations, and compact evidence export. The [saved functional capture](devnet/captures/2026-10-03/README.md)
records eight successful proof-bearing blocks and two unsuccessful 64-signature attempts.
A separate [Kurtosis setup](devnet/consensus/README.md) uses real Lighthouse beacon/validator processes
through a local Engine proof-field relay; three small proof cases have successful receipts on both ELs.
These checks preserve their deployed source/binary provenance and do not replace the archived
benchmark samples or establish 64-signature inclusion, native CL proof-field support, or throughput.

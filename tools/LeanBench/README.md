# Lean proof and devp2p benchmarks

This standalone tool measures the pinned native leanVM/SPHINCS backend and production RLPx Snappy/AES/MAC codecs over localhost TCP. It uses a real production transaction pool through the test node's DI setup. Session secrets are preset; capability exchange, Internet latency and TCP/IP headers are outside the wire-byte measurement.

Requires .NET 10 and Rust 1.99. Run from the repository root:

```sh
cargo run --manifest-path tools/lean-ffi/Cargo.toml --release --locked --example bench_fixtures -- /tmp/lean-bench-vectors 2048 1200
dotnet run --project tools/LeanBench -c Release -p:BuildLeanFfi=true -- --fixtures=/tmp/lean-bench-vectors --out=/tmp/lean-bench-results
python3 tools/LeanBench/plot.py /tmp/lean-bench-results/results.json --output=/tmp/lean-bench-results/plots
```

The fixture generator cycles sixteen deterministic SPHINCS keys and signs distinct messages containing fixture identity and nonce. Generic STARK proofs have distinct public inputs and pinned program commitments. Generation is outside timed rows and its cost is saved separately (per-proof CSV and overall generation metadata). Every timed crypto/load batch actually proves fresh distinct claims. Increasing duration/rates can require more fixtures; exhaustion fails the run.

- `crypto`: sequential proving and verification of 1/4/8/16 signatures, one generic STARK, mixed claims, and block aggregates of 64/128 signatures. Default three warmups and five measured batches.
- `load`: open-loop batch arrivals through a bounded FIFO, native proving, localhost transport, proof verification and actual pool admission. Default ten seconds at 1/10/25/50/100 offered transactions per second. Full queues drop whole batches. Shared-signature transactions are labeled separately.
- `protocol-preproved`: distinct proofs prepared before timing, then delivered through the actual negotiated lean/1 receive handler and background scheduler. Measures its one-active/one-latest queue, including replacement drops. Preparation time is reported. Pool admission events capture accepted hashes and consume accepted entries without executing them.
- `transport`: random incompressible objects of 1KiB/1MiB/10MiB over the same encrypted TCP pipeline. Default 1/10/25/50 offered objects per second. Objects above the shared 10MiB wrapper bound are rejected.

Transactions use a secp256k1 validation prefix and real Lean dependency claims; the load rows measure proof-bearing admission. Accepted pool entries are removed so funding, nonce gaps and pool capacity do not dominate sustained admission. Chain state remains fixed; this measures admission rather than transaction execution. Every independent case/load starts with a fresh node. Useful transaction goodput counts unique admitted transaction RLP bytes, payload throughput includes proofs/wrappers, and wire throughput counts encrypted RLPx bytes. Latency runs from scheduled arrival through admission and includes queueing; protocol percentiles include only accepted batches and end at the last admitted transaction in each batch; duration includes drain. At low loads batch quantization makes actual offered load differ from the requested rate.

`results.json`, `results.csv`, and per-batch `samples.csv` retain counts, proof sizes, goodput, payload/wire throughput, drops/rejections, latency and CPU. `processCpuMs / (durationSeconds * 1000)` gives average CPU cores used. Load-stage process CPU intervals overlap and must not be summed; sequential crypto stage measurements can be compared directly.

For a short smoke run:

```sh
dotnet run --project tools/LeanBench -c Release -p:BuildLeanFfi=true -- --fixtures=/tmp/lean-bench-vectors --out=/tmp/lean-bench-smoke --seconds=1 --warmups=1 --repetitions=1 --rates=10 --cases=sphincs1,sphincs16,stark1,mixed16 --protocol-rates=10 --object-rates=1 --object-sizes=10485760
```

Controls use `--name=value`: `seconds`, `queue`, `warmups`, `repetitions`, `rates`, `cases`, `block-counts`, `protocol-cases`, `protocol-rates`, `object-rates`, and `object-sizes`. Empty case lists disable that group. The loopback result is a local CPU/codec/admission baseline, not a prediction of WAN throughput.

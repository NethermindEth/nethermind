# Generic duplicate normalization: exploratory capture

Measured source `1c328c733c03fc38dd4e35fce39ba1720080004e`; leanVM `f33f31bf7c1191667e29a68a3acae63b9164c1c6`; Apple M2 Max, 32 GiB, macOS 15.7.4, ARM64, .NET 10.0.9. One warmup and three measured repetitions per case, executed sequentially. [Raw JSON and provenance](results.json) · [Nine sample rows](samples.csv) · [Capture checksums](sha256sums.txt).

Two genuine CPU proofs use the same public claim and program commitment with expansion factors 1/2. Their valid witness lengths are 238,160 and 290,056 bytes; a different genuine claim supplies the second dependency. All witnesses and prepared parents were authenticated before timing. Timed work calls the actual managed `RecursiveStarkAggregator.Prove` with the native backend, including normalization, child merges and final proving. Final verification is measured separately. Parent preparation took 43.28 ms and is excluded from fold timing; fixture generation is also excluded.

| Input | Native prove calls | Mean fold wall time | Mean final verification |
| --- | ---: | ---: | ---: |
| Two unique parents: short A, B | 1 | 10.79 ms | 10.52 ms |
| Three parents: short A, B, identical short A | 3 | 32.35 ms | 11.55 ms |
| Three distinct parents: long A, B, short A | 3 | 24.06 ms | 10.49 ms |

Every result verified and retained exactly the shortest A witness plus unique B, with a 530,928-byte final envelope. The two-parent reference uses the direct fast path. The three-parent cases invoke the bounded slow fold; pruning removes every nonselected generic source, including equal-length ties, before merging. Their three native calls comprise pruning, one intermediate merge and the final proof.

The equal-short row intentionally supplies an identical parent twice to isolate a controlled direct-input shape. Production `Combine` deduplicates identical parent hashes, so that row is not a production duplicate workload. The distinct long/short row exercises the actual different-length normalization case. Parent position changes which claims survive into intermediate proofs, and the sequential three-sample run cannot establish a causal incremental pruning cost or show that longer witnesses are faster. These exploratory timings do not establish production throughput, stable tail latency, a near-8 MiB capacity edge, signature aggregation cost, or generic recursive compression. No pool or transport work is timed.

## Reproduce

From the repository root, using the recorded source revision:

```sh
(cd tools/lean-ffi && RUSTFLAGS='-C target-cpu=native' cargo build --release --locked --example bench_fixtures)
tools/lean-ffi/target/release/examples/bench_fixtures /tmp/lean-duplicate-vectors 0 0 --duplicate-generic
dotnet build tools/LeanBench/LeanBench.csproj -c Release -p:BuildLeanFfi=true --disable-build-servers -m:1 -p:CheckForOverflowUnderflow=false
dotnet tools/artifacts/bin/LeanBench/release/LeanBench.dll --duplicate-normalization=true --fixtures=/tmp/lean-duplicate-vectors --out=/tmp/lean-duplicate-results --warmups=1 --repetitions=3 --source-revision=1c328c733c03fc38dd4e35fce39ba1720080004e '--hardware=Apple M2 Max; 32 GiB'
```

The JSON retains the exact captured command, all managed/native binary SHA-256 hashes and the four fixture hashes. Absolute paths describe the original host invocation. Proof fixture binaries are intentionally untracked; generation produces authentic proofs rather than padded stand-ins. Repeated captures may differ; retain their new source, binary hashes and timings separately. Earlier Daisugi captures remain unchanged.

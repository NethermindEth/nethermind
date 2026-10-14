# Native proof cost probe

This probe uses real signatures, real CPU proofs and the production native adapter's bounded aggregation. It changes no guest or prover policy. It must be built from a checkout containing the domain/profile alignment in `04da763081`, with fork `cf1e92f87a7aec052b680f73e56145e866218583` and guest key `00f24bbdf44a31139c975223b77510cd93a905664da3c4a52b5eba4e9b07de9e`.

```sh
export LEANVM_NUM_THREADS=1
export CARGO_BUILD_JOBS=2
export CARGO_TARGET_DIR=/tmp/lean-native-cost-target
export RUSTFLAGS='-C target-cpu=native'
unset ZK_ALLOC_POISON
cargo +1.99.0 build --release --manifest-path scripts/lean-net-bench/native-cost/Cargo.toml
python3 scripts/lean-net-bench/native-cost/capture.py \
  --exe /tmp/lean-native-cost-target/release/lean-native-cost \
  --out /tmp/lean-native-cost-new-run --repetitions 3 --include-sphincs64
```

Run one probe process at a time. `capture.py` uses `os.wait4` peak resident memory, converting Linux KiB to bytes; macOS already reports bytes. The peak covers the entire fresh process, including fixtures and verification, and is not stage-specific memory. No timing assertions are made. The output directory must be new. Parent preparations and proof files remain local; archive only top-level CSV, JSON and text logs, not `.bin` files or generated fixture directories.

The `proving_ms` interval calls the real adapter, including input authentication, native leaves and recursive merges. Fixture generation and prepared-parent construction are separate fields. Root verification is a separate interval. SPHINCS 16/64 are actual trees with four-signature raw leaves, not direct 16/64-signature prover calls. Mixed raw input is processed through the adapter's disjoint SPH/generic leaves and merge. `mixed-merge` proves from precomputed SPH1 and generic1 parents in its fresh process.

Each raw case additionally verifies the direct witness batch ten times, reporting mean batch cost and repetition count in its text log. This is distinct from verification of the final recursive root. The generic program has two fixed public-input cells, nonzero bits in both 128-bit halves, and default 2048-instruction compiled geometry. It exercises genuine generic recursion but does not measure worst-case arbitrary bytecode, the maximum inner committed/bus geometry, or justify a universal leanSTARK-to-SPHINCS weight of 16.

The pinned guest supports 256 total dependencies, 16 generic dependencies, at most 2048 instructions per generic program, inner committed/bus logs at most 22, mixed committed log at most 27 and bus log at most 26, rate 1 and its bounded transcript stream. These limitations prevent claiming full conformance to the companion's 4096-dependency aggregate capacity. Measurements are not RAM guarantees.

The archived 2026-10-14 captures used the identical `src/main.rs` via a temporary manifest pointing to the committed adapter and fork checkouts. Their original absolute command paths are retained as immutable capture provenance; the commands above provide portable reproduction. Their historical executable hash belongs to that temporary build, not a promise that a differently located build produces the same binary hash. Three fresh-process samples were recorded for the small cases; SPH64 is an exploratory single sample. Managed builds/tests also ran on the host during part of capture; the host was not reserved exclusively for timing.

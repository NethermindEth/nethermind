# nethermind-evm-ffi

Nethermind's EVM as a **redistributable native package** with a C ABI and Rust bindings, so a
Rust program — rbuilder in particular — can execute transactions with it.

**Setting this up for the first time? Follow [GUIDE.md](GUIDE.md)** — prerequisites, clone, build,
verify, consume from your own crate, and benchmark, in order. This README is the reference.

The package splits in two, and the split is the point:

| built here, needs the repo and a .NET SDK | consumed anywhere, needs neither |
| --- | --- |
| `Nethermind.Evm.Ffi` → a NativeAOT shared library | `nethermind-evm-sys` → a Rust crate that links a prebuilt bundle |

A bundle is **24 MB**, links against nothing but libc and libm, and carries the header it was
built from. Nothing about consuming it involves .NET.

## Build a bundle

```bash
tools/nethermind-evm-ffi/package.sh
```

Needs the same prerequisites as any NativeAOT build — a .NET SDK matching `global.json`, gcc, and
static `libz`/`libstdc++`. `tools/evm-native-bench/run.sh check` reports them.

Output, under `build/nethermind-evm-<version>-linux-x64/`:

```
include/nethermind_evm.h      the ABI contract
lib/libnethermind_evm.so      the engine
lib/lib{blst,secp256k1,secp256r1,gmp,mcl}.so, ckzg.so, kzg_trusted_setup.txt
                              what the precompiles dlopen on first use
MANIFEST                      version, ABI number, RID, build time, commit
```

Set `NM_EVM_BUNDLE_DIR` to put it elsewhere, `NM_EVM_VERSION` to stamp a version.

## Consume it

```bash
export NETHERMIND_EVM_DIR=/path/to/nethermind-evm-0.1.0-linux-x64
cargo run --release -p nethermind-evm-smoke
```

In a consumer's `Cargo.toml`:

```toml
[dependencies]
nethermind-evm-sys = { path = "…/rust/nethermind-evm-sys" }   # or a git dependency
```

and a `build.rs`, which is the one piece of boilerplate a consumer cannot avoid — Cargo does not
propagate link arguments across crates, so the final binary has to bake in the rpath itself:

```rust
fn main() {
    let lib = std::env::var("DEP_NETHERMIND_EVM_LIB").unwrap();
    println!("cargo:rustc-link-arg=-Wl,-rpath,{lib}");
}
```

`DEP_NETHERMIND_EVM_LIB` and `DEP_NETHERMIND_EVM_BUNDLE` reach direct dependents because the sys
crate declares `links = "nethermind_evm"`. Without the rpath the binary builds and then fails at
startup with `libnethermind_evm.so: cannot open shared object file`.

`build.rs` finds the bundle from `NETHERMIND_EVM_DIR`, or from `vendor/` beside the sys crate if
you would rather check one in.

## Same source as the node

The EVM in the bundle is compiled from this repository's own sources — `Nethermind.Evm.Ffi.csproj`
takes `ProjectReference`s on `Nethermind.Evm`, `Nethermind.Evm.Precompiles`, `Nethermind.State`,
`Nethermind.Specs` and `Nethermind.Blockchain`. Not a NuGet package, not a vendored copy, and with
`EnableZkEvm` unset so the same `.std.cs` variants build that the node builds.

Two things that follow, and one that does not:

- **Same IL, different code generator.** The bundle is NativeAOT; the node runs the JIT. Same
  semantics, different machine code.
- **A bundle is a loose binary.** Once copied it has no inherent link to the tree it came from, so
  a builder could pair a stale EVM with a node on another revision and nothing would notice. That
  is a consensus hazard, so the revision is stamped into the library and readable at run time:

  ```
  nethermind-evm 0.1.0 commit=bf3411dddafd63eead3bc57ab2b8a79ec7f338c3 abi=1
  ```

  `nm_evm_build_info()` in C, `nethermind_evm_sys::build_info()` in Rust. **A host that also runs a
  Nethermind node should compare this commit against the node's and refuse to build on a
  mismatch.** `package.sh` refuses to produce a bundle whose revision it cannot determine
  (override with `NM_EVM_COMMIT=<sha>`, as CI does when building from an exported tree, or
  `NM_EVM_ALLOW_UNSTAMPED=1` to accept one deliberately), and a tree that is modified — or that
  cannot be inspected at all — is stamped `-dirty`, because "we did not check" must not read as
  "clean".
- **It does not make the two builds bit-identical.** Nothing here reproduces the node binary; it
  guarantees only that both were compiled from one named revision. Building the node image and the
  bundle in the same CI job from one checkout is what keeps that cheap to honour.

## The shape of the API

The host owns the state. The engine borrows through four callbacks, executes one transaction per
call, and hands back what changed. It builds no trie and computes no state root — the caller
applies the diff to its own state and derives roots itself, which is exactly what rbuilder already
does with `BundleState`.

```c
NmEvmEngine *e = nm_evm_engine_new(&host, 1);   /* host = {ctx, get_account, get_storage,
                                                            get_code, get_block_hash} */
nm_evm_set_block(e, &block);                    /* also fixes the fork */
NmEvmResult r = {0};
nm_evm_execute(e, &tx, NM_EXEC_DEFAULT, &r);    /* r: success, gas, output, accounts,
                                                       storage, code, logs */
nm_evm_result_free(&r);
nm_evm_engine_free(e);
```

One transaction is one crossing. Everything a result points at lives in a single allocation that
`nm_evm_result_free` releases. Changes come back in the order execution made them and must be
applied in that order — an address can appear more than once.

Read `include/nethermind_evm.h`; it is the contract and it is commented.

## Limits of v1

- **linux-x64 only.** Other RIDs need a matching NativeAOT publish; nothing in the design is
  platform-specific.
- **Mainnet rules only.** `nm_evm_engine_new` refuses any `chain_id` but 1 rather than apply
  mainnet's fork schedule to another chain. Supporting more means a chainspec-driven spec provider.
- **No blob or authorisation lists.** `NmEvmTx` carries the 4844 and 7702 type tags but not their
  payloads, so those envelopes are accepted as the wrong shape. Add the fields before using them.
- **No access lists.** EIP-2930 lists are not passed, so a 2930 transaction is charged as if it
  declared none.
- **One engine per thread.** `ITransactionProcessor` and `IVirtualMachine` hold mutable per-block
  context. Engines share no managed state, but they do share one garbage collector — see the
  benchmark package for what that costs in the tail.

## Verifying a bundle

`rust/smoke` is the end-to-end check and doubles as the worked example: it stands up a toy host
with a funded sender and a contract that writes storage and emits a log, then asserts the engine
comes back with 21,000 gas for a transfer, one storage change, one log, and a non-zero count of
callbacks into the host.

```
transfer     success=1 gas_used=21000 accounts=3 storage=0 logs=0
contract     success=1 gas_used=43865 accounts=2 storage=1 logs=1
host callbacks: account=4 storage=1 code=1
```

`cargo test -p nethermind-evm-sys` asserts the Rust struct sizes against the C header and that the
library's `nm_evm_abi_version()` matches the bindings — a layout drift between the two is silent
corruption otherwise.

## What is deliberately not in the bundle

A naive publish is 105 MB. Three quarters of it is unreachable from the EVM and arrives through
`Nethermind.Blockchain`'s package graph, which the EVM needs only because
`EthereumPrecompileProvider` and `EthereumCodeInfoRepository` live there:

- `ClearScriptV8.linux-x64.so`, 58 MB — V8, for the JavaScript tracer
- `libic.so`, 6 MB — TurboPFor integer compression, for receipt storage

Neither is dlopened unless something calls into it and nothing here does. Moving those two types
down into `Nethermind.Evm` would cut the dependency at the source; until then `package.sh` names
what it copies explicitly, so a new native shows up as a missing symbol rather than silently
riding along.

## Relationship to tools/evm-native-bench

The benchmark package came first and measures the same library against revm. It has its own
harness with a deliberately benchmark-shaped ABI (`run(handle, count, workload)`); this package is
the real contract. They share the NativeAOT setup and the `ILLink.Descriptors.xml` that keeps the
RLP decoders alive through trimming — see that README for the traps.

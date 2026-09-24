# Running Nethermind's EVM from Rust — from source

A complete walkthrough, from an empty machine to a Rust program executing transactions on
Nethermind's EVM, and then benchmarking it against revm. Every command here was run, in this
order, from a fresh clone.

There are two packages in `tools/`, and you need the first to use the second:

| | what it is | you run it to |
| --- | --- | --- |
| `tools/nethermind-evm-ffi` | the EVM as a native library, a C header and Rust bindings | build something on it |
| `tools/evm-native-bench` | a benchmark harness around the same library, plus revm | measure it |

Time: about 15 minutes the first time, mostly downloads and compilation; later runs take under a
minute. Disk: about 5 GB, including the .NET SDK, the Rust toolchain and the package caches.

---

## 1. Platform

**Linux x64.** On Windows, use **WSL2 with Ubuntu 22.04 or newer** and do *everything* inside it —
including the clone. Building from a checkout under `/mnt/c` works but is several times slower, and
the timings it produces are not worth reading.

macOS and Windows-native builds are not supported: the bundle is `linux-x64` only.

Everything below assumes a bash shell on Ubuntu.

---

## 2. Install the prerequisites

### 2.1 System packages

```bash
sudo apt update
sudo apt install -y git curl build-essential zlib1g-dev
```

`build-essential` provides gcc and the static `libstdc++.a`; `zlib1g-dev` provides `libz.a`. NativeAOT
links both into the library. clang is **not** required — the project tells the compiler to drive gcc.

> **No sudo?** Check whether they are already there before asking for it:
> `gcc --version && ls /usr/lib/x86_64-linux-gnu/libz.a /usr/lib/gcc/x86_64-linux-gnu/*/libstdc++.a`.
> Many developer images have all three. Everything from here on installs into your home directory.

### 2.2 .NET SDK

`global.json` requires **10.0.300 or later**. Distro packages are usually older, so install
per-user:

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$HOME/.dotnet"
```

Put it on your path (add these two lines to `~/.bashrc` to make it permanent):

```bash
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
```

```bash
dotnet --version        # expect 10.0.3xx or later
```

The build scripts prefer `~/.dotnet` automatically when it exists, so a stale system SDK on the path
will not be picked up by accident.

### 2.3 Rust

```bash
curl https://sh.rustup.rs -sSf | sh -s -- -y --profile minimal
source "$HOME/.cargo/env"
cargo --version
```

### 2.4 dotnet-trace (optional)

Only needed for the allocation profile in step 7.3:

```bash
dotnet tool install --global dotnet-trace
```

---

## 3. Get the source

The work is on the branch **`feature/nethermind-evm-ffi`**. The submodules (`src/tests`,
`src/bench_precompiles`) are not needed, so a shallow clone without them is enough:

```bash
git clone --depth 1 --branch feature/nethermind-evm-ffi \
  https://github.com/NethermindEth/nethermind.git ~/nethermind
cd ~/nethermind
```

> **If the branch is not on GitHub yet**, clone it straight from a local checkout instead. From WSL,
> a Windows checkout is reachable under `/mnt/c`:
>
> ```bash
> git clone --depth 1 --branch feature/nethermind-evm-ffi \
>   file:///mnt/c/Users/<you>/Documents/GitHub/nethermind ~/nethermind
> ```
>
> Use `file://`; a bare path ignores `--depth`.

Confirm the prerequisites in one go:

```bash
tools/evm-native-bench/run.sh check
```

It lists the SDK, cargo, gcc, the two static libraries and dotnet-trace, and warns if the checkout
sits on a Windows drive.

---

## 4. Build the bundle

```bash
tools/nethermind-evm-ffi/package.sh
```

This compiles `Nethermind.Evm` — **from this checkout's `src/Nethermind`, the same sources the node
builds** — ahead of time into a native shared library, and assembles a self-contained bundle. It
takes a few minutes the first time, while packages download; about 25 seconds after that.

What it prints, in order:

```
=== provenance ===
  commit   1a2b3c4d5e6f…      the revision being compiled
  dirty    false              whether the tree has uncommitted changes

=== bundle ===
  24M  …/build/nethermind-evm-0.1.0-linux-x64

=== what the library says about itself ===
  nethermind-evm 0.1.0 commit=<40-char sha> abi=1
```

The last line is read **out of the compiled library**, not from a file next to it. That is the
point: a bundle is a loose binary, and this is the only thing that still ties it to a revision once
it has been copied somewhere.

The bundle lands in `tools/nethermind-evm-ffi/build/nethermind-evm-0.1.0-linux-x64/`:

```
include/nethermind_evm.h          the C ABI — read this, it is the contract
lib/libnethermind_evm.so          the EVM
lib/libblst.so libsecp256k1.so    native crypto the precompiles load on first use
    libsecp256r1.so libgmp.so
    libmcl.so ckzg.so
    kzg_trusted_setup.txt
MANIFEST                          version, ABI, commit, dirty flag, build time
```

It depends on nothing but libc and libm. You can copy the directory anywhere — to another machine,
into a container, into your own repository — and consume it without a .NET SDK.

### Options

| variable | effect |
| --- | --- |
| `NM_EVM_BUNDLE_DIR=/path` | put the bundle somewhere else |
| `NM_EVM_VERSION=1.2.3` | stamp a version (default `0.1.0`) |
| `NM_EVM_COMMIT=<sha>` | supply the revision when building from a tree with no `.git`, as CI does from an exported archive |
| `NM_EVM_DIRTY=true\|false` | supply the dirty flag in the same situation |
| `NM_EVM_ALLOW_UNSTAMPED=1` | build even when the revision cannot be determined — see below |

**`package.sh` refuses to build when it cannot determine the revision.** That is deliberate: a bundle
nobody can match to a node is a consensus hazard, not an inconvenience. If you hit it, you are almost
certainly building from a copy of the tree rather than a clone; pass `NM_EVM_COMMIT`.

**A modified tree is stamped `-dirty`,** and so is one whose status cannot be checked at all. A dirty
bundle matches no published revision and should never be paired with a released node.

---

## 5. Check that it works

```bash
cd tools/nethermind-evm-ffi/rust
export NETHERMIND_EVM_DIR=~/nethermind/tools/nethermind-evm-ffi/build/nethermind-evm-0.1.0-linux-x64

cargo test --release -p nethermind-evm-sys
cargo run  --release -p nethermind-evm-smoke
```

The tests assert that the Rust struct layouts match the C header, and that the library's ABI version
matches the bindings — a drift there is silent memory corruption otherwise.

The smoke test stands up a toy host with a funded account and a contract that writes a storage slot
and emits a log, then runs a transfer and a contract call through the engine:

```
library ABI 1, bindings expect 1
nethermind-evm 0.1.0 commit=<sha> abi=1

transfer     success=1 gas_used=21000 accounts=3 storage=0 logs=0
contract     success=1 gas_used=43865 accounts=2 storage=1 logs=1
  storage    0x3333..[1] = 42
  log        address=0x3333.. topic0=0xaa

host callbacks: account=4 storage=1 code=1

OK
```

21,000 gas for a plain transfer, one storage write, one log, and a non-zero count of calls back into
the host — which proves the engine is reading *your* state rather than its own.

---

## 6. Use it from your own Rust project

### 6.1 Depend on the bindings

```toml
# Cargo.toml
[dependencies]
nethermind-evm-sys = { path = "/home/<you>/nethermind/tools/nethermind-evm-ffi/rust/nethermind-evm-sys" }
```

(or a `git = …, branch = "feature/nethermind-evm-ffi"` dependency once the branch is pushed.)

### 6.2 Add a build script — this is required

Cargo does not pass one crate's linker arguments on to the crates that depend on it, so your binary
has to record where the library lives. Without this it compiles cleanly and then fails at start-up
with `libnethermind_evm.so: cannot open shared object file`.

```toml
# Cargo.toml
[package]
build = "build.rs"
```

```rust
// build.rs
fn main() {
    let lib = std::env::var("DEP_NETHERMIND_EVM_LIB").unwrap();
    println!("cargo:rustc-link-arg=-Wl,-rpath,{lib}");
}
```

`DEP_NETHERMIND_EVM_LIB` exists because the sys crate declares `links = "nethermind_evm"`; Cargo hands
it to direct dependents only.

### 6.3 Tell the build where the bundle is

Either set `NETHERMIND_EVM_DIR` when you build (as in step 5), or copy the bundle to
`nethermind-evm-sys/vendor/` and build with nothing set.

### 6.4 The minimal program

`tools/nethermind-evm-ffi/rust/smoke/src/main.rs` is the complete worked example; the shape is:

```rust
use nethermind_evm_sys::*;

// 1. Implement four callbacks over your state: get_account, get_storage, get_code, get_block_hash.
//    Each receives the `ctx` pointer you register, so it can reach your own state object.
let host = NmEvmHost { ctx, get_account: Some(..), get_storage: Some(..),
                       get_code: Some(..), get_block_hash: Some(..) };

// 2. One engine per thread. v1 accepts chain id 1 only.
let engine = unsafe { nm_evm_engine_new(&host, 1) };

// 3. The block fixes the fork, from its number and timestamp.
unsafe { nm_evm_set_block(engine, &block) };

// 4. One call per transaction; the result carries the diff.
let mut result = NmEvmResult::default();
let rc = unsafe { nm_evm_execute(engine, &tx, NM_EXEC_DEFAULT, &mut result) };
// rc != NM_OK  → the transaction never ran (rejected, or an internal error — see nm_evm_last_error)
// rc == NM_OK  → it ran; result.success says whether it succeeded or reverted

// 5. Apply result.accounts / storage / code IN ORDER to your state, read result.logs, then:
unsafe { nm_evm_result_free(&mut result) };
unsafe { nm_evm_engine_free(engine) };
```

Things that are easy to get wrong:

- **All 256-bit values are 32 bytes little-endian.** Addresses and hashes are big-endian, as on chain.
- **An account with no code must report `keccak256("")`** as its code hash, or the engine goes
  looking for code that does not exist.
- **Apply changes in the order they are returned.** The same address can appear more than once, and a
  deletion can be followed by writes to it.
- **The engine computes no state root.** It hands you a diff; you derive roots, exactly as rbuilder
  already does with `BundleState`.

### 6.5 Check you are running the EVM you think you are

If your process runs beside a Nethermind node, compare the two revisions at start-up and refuse to
build on a mismatch — two EVMs from different revisions can disagree, and the disagreement surfaces
as an invalid block, not an error.

```rust
let evm = nethermind_evm_sys::build_info();   // "nethermind-evm 0.1.0 commit=1a2b3c4d5e6f<…> abi=1"
```

The node reports its revision through `web3_clientVersion`:

```bash
curl -s -X POST localhost:8545 -H 'content-type: application/json' \
  -d '{"jsonrpc":"2.0","id":1,"method":"web3_clientVersion","params":[]}'
# {"result":"Nethermind/v1.36.0+1a2b3c4d/linux-x64/dotnet10.0.x", …}
```

**The node shows only the first 8 characters of the commit** (after the `+`), while the bundle shows
all 40. Compare prefixes. Refuse outright if the bundle's string ends in `-dirty`.

---

## 7. Run the benchmarks

These measure the same library against revm 38.0.0 (the version rbuilder pins) on your machine.

### 7.1 Everything

```bash
cd ~/nethermind
tools/evm-native-bench/run.sh all
```

That is `check`, then the Nethermind suite, then the revm baseline. It builds its own copy of the
library (a benchmark-shaped variant, not the bundle from step 4) under `~/.cache/evm-native-bench`.

### 7.2 One piece at a time

```bash
tools/evm-native-bench/run.sh nethermind   # the Nethermind EVM, driven from Rust
tools/evm-native-bench/run.sh revm         # revm on the same workloads
tools/evm-native-bench/run.sh publish      # just build the library
```

### 7.3 Allocation profile

```bash
tools/evm-native-bench/run.sh trace
```

Needs dotnet-trace. Runs the workloads as an ordinary JIT process under `dotnet-trace`, then prints
allocation by type and the top allocation call stacks.

### 7.4 Reading the output

```
B callbk  fresh recipient   8t | 2103439 tx/s | mean 3.65 p50 0.83 p99 3.92 max 8070.51 us | 1.01 reads/tx | alloc 1360 B/tx | gc 13/4/2
```

| column | meaning |
| --- | --- |
| `A memdb` / `B callbk` | state held inside the EVM / state held by Rust and read through callbacks (the rbuilder shape) |
| `8t` | threads, one engine each, one garbage collector shared by all |
| `mean` vs `p50` | **the gap between them is the GC.** revm's two agree to the decimal; Nethermind's mean sits well above its median. Quote both. |
| `max` | the single worst transaction — a collection pause. Expect milliseconds. |
| `reads/tx` | callbacks into Rust per transaction. ~1 on the fresh-recipient rows proves the boundary is crossed. |
| `alloc B/tx` | managed allocation per transaction; this is what drives the tail |
| `gc a/b/c` | gen0 / gen1 / gen2 collections during the measured window |

The two workloads exist to separate two costs: a **value transfer** is almost entirely
per-transaction machinery, a **10,000-iteration loop call** is almost entirely interpreter. Compare
each against the matching revm line, never across workloads.

Timings from WSL2 on a laptop are useful for ratios between engines measured back to back, and not
much else.

---

## 8. Building the node from the same revision

The guarantee in 6.5 is only as good as your discipline about revisions. The simplest way to keep it:
**build the node and the bundle from one checkout, in one go.**

```bash
cd ~/nethermind
git rev-parse HEAD                                        # note it
dotnet build src/Nethermind/Nethermind.Runner -c release  # the node
tools/nethermind-evm-ffi/package.sh                        # the bundle, same tree
```

Both now carry that commit — the node in `web3_clientVersion`, the bundle in `nm_evm_build_info()`.
Rebuilding one without the other is how they drift.

---

## 9. Troubleshooting

| symptom | cause | fix |
| --- | --- | --- |
| `Permission denied` running a script | cloned from a Windows checkout that lost the executable bit | `bash tools/nethermind-evm-ffi/package.sh`, or `chmod +x` the script |
| `A compatible .NET SDK was not found` | system SDK older than 10.0.300 | step 2.2; make sure `~/.dotnet` comes first on `PATH` |
| `cannot determine the Nethermind revision` | building from a copy, not a clone | build from a clone, or pass `NM_EVM_COMMIT=<sha>` |
| publish fails linking, mentions `libz` or `libstdc++` | a static library is missing | `sudo apt install build-essential zlib1g-dev` |
| `no Nethermind EVM bundle found` from cargo | `NETHERMIND_EVM_DIR` unset or wrong | point it at the directory containing `lib/` and `include/` |
| builds, then `libnethermind_evm.so: cannot open shared object file` | the rpath build script is missing | step 6.2 |
| `ABI mismatch` in the smoke test | bindings and library from different revisions | rebuild the bundle from the checkout the crate came from |
| `nm_evm_engine_new` returns null | chain id other than 1, or a null callback | v1 is mainnet-only; all four callbacks are required |
| `nm_evm_execute` returns `NM_ERR_TX_REJECTED` | invalid transaction — nonce, balance, fee | `nm_evm_last_error()` says which |
| wildly optimistic benchmark numbers | the benchmark ran against a stale library | `run.sh` refuses to after a failed build; do not bypass it |
| everything is slow | checkout under `/mnt/c` | clone inside the Linux filesystem (step 1) |

---

## 10. What v1 does not do

- **linux-x64 only.**
- **Mainnet only.** `nm_evm_engine_new` rejects any chain id but 1 rather than apply mainnet's fork
  schedule to another chain.
- **No blob hashes, authorisation lists or access lists.** `NmEvmTx` carries the EIP-4844 and
  EIP-7702 type numbers but not their payloads, and has no EIP-2930 access list. Transactions of those
  types would execute with the wrong charges. Add the fields before feeding the engine real mempool
  traffic.
- **One engine per thread**, all sharing one garbage collector. The benchmark shows what that costs in
  the tail.

The contract for everything above is `include/nethermind_evm.h`. The design rationale and the list of
known traps are in `README.md` beside this file and in `tools/evm-native-bench/README.md`.

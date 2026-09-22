# evm-native-bench

Publishes `Nethermind.Evm` as a **NativeAOT shared library**, drives it from **Rust** across a C ABI,
and measures it against **revm** on the same machine. It exists to answer whether Nethermind's EVM
could serve as the execution engine inside a Rust block builder such as
[rbuilder](https://github.com/flashbots/rbuilder).

Everything is scaffolding for a measurement — nothing here ships, and nothing in `src/` is modified.

## What it measures

Two workloads, chosen because they separate two costs a single blended number hides:

| workload | what dominates |
| --- | --- |
| **value transfer** (21k gas, no bytecode) | per-transaction machinery: validation, journaling, commit |
| **10,000-iteration loop call** (~311k gas, 70k opcodes) | the interpreter |

Two ways of supplying state, to price the FFI boundary:

| mode | state lives in | how the EVM reads it |
| --- | --- | --- |
| **A** | the EVM (`MemDb` + trie) | in-process, as the client does |
| **B** | Rust | three C callbacks — `getAccount`, `getStorage`, `getCode` |

Mode B is the rbuilder-shaped design: the host owns state, the EVM borrows it and returns a diff,
and no trie or state root is computed on the managed side.

## Prerequisites

Linux x64 (WSL2 is fine). `./run.sh check` reports all of these.

- **.NET SDK** matching `global.json` (>= 10.0.300). A distro package is often older; install
  per-user and it will be picked up automatically:
  ```bash
  curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir "$HOME/.dotnet"
  ```
- **gcc** plus static `libz.a` and `libstdc++.a` — ILC drives a C toolchain to link the library.
  `sudo apt install build-essential zlib1g-dev` covers it. (clang also works; the project selects
  gcc via `CppCompilerAndLinker` so clang is not required.)
- **Rust** (stable), installable without root:
  ```bash
  curl https://sh.rustup.rs -sSf | sh -s -- -y --profile minimal --no-modify-path
  ```
- **dotnet-trace**, only for `run.sh trace`:
  ```bash
  dotnet tool install --global dotnet-trace
  ```

## Running

```bash
tools/evm-native-bench/run.sh check        # prerequisites
tools/evm-native-bench/run.sh all          # publish + Nethermind suite + revm baseline
tools/evm-native-bench/run.sh nethermind   # publish + the Rust-driven suite only
tools/evm-native-bench/run.sh revm         # the revm baseline only
tools/evm-native-bench/run.sh trace        # allocation profile of the JIT harness
```

If git did not preserve the executable bit (common on a Windows checkout), prefix with `bash`.

Build output and traces go to `$HOME/.cache/evm-native-bench` (override with `EVM_BENCH_OUT`).
First run takes a few minutes: it builds the Nethermind dependency graph, runs ILC, and compiles
revm from source.

> **Run from a Linux-native path.** On a checkout under `/mnt/c` the build is far slower and the
> timings are not trustworthy. `run.sh check` warns when it detects this.

## Reading the output

```
B callbk  fresh recipient   8t | 2103439 tx/s | mean 3.65 p50 0.83 p99 3.92 max 8070.51 us | 1.01 reads/tx | alloc 1360 B/tx | gc 13/4/2
```

- `mean` vs `p50` — the gap between them *is* the GC. revm's mean and median agree to two decimals;
  Nethermind's mean runs well above its median. Quote both or you will mislead someone.
- `max` — the worst single transaction, which is a collection pause. Expect milliseconds.
- `reads/tx` — callbacks into Rust per transaction, counted on the managed side. ~1.00 on the
  fresh-recipient workload confirms the boundary is actually being crossed; 0.00 on the others means
  the scope cache served everything.
- `gc a/b/c` — gen0/gen1/gen2 collections during the measured window.

## Things that will bite you

- **`LimboLogs` is not a silent logger.** `LimboTraceLogger.IsTrace` is hard-coded `true` so that
  tests exercise log-message construction. Benchmarking with it measures interpolated trace strings:
  on the transfer workload that was **77% of all allocation** and roughly half the wall clock. These
  harnesses use `NullLogManager.Instance`. So does `Nethermind.Stateless.Executor`.
- **The block header picks the fork.** A header with number 1 selects *Frontier*. Both harnesses pin
  Prague (`ParisBlockNumber + 3`, `PragueBlockTimestamp`) to match revm.
- **A failed publish leaves a stale `.so` behind** and the harness will happily benchmark it. `run.sh`
  deletes the artifact before publishing and refuses to continue if the publish fails. Keep that
  property if you edit it.
- **NativeAOT emits `Nethermind.Evm.Native.so`** with no `lib` prefix, so `-l` cannot find it;
  `run.sh` symlinks `libNethermind.Evm.Native.so`. Exports also carry a `@@V1.0` version suffix,
  which a plain `extern "C"` declaration resolves fine.
- **The JIT harness needs a long warm-up.** 3.3 us/transfer at 20k iterations against 1.68 us at
  200k — tiered compilation. Do not compare a short JIT run with an AOT run.
- **`ILLink.Descriptors.xml` is load-bearing.** `Rlp`'s static constructor reflects over its own
  assembly to find `IRlpDecoder<T>` implementations and `Activator`-constructs them; ILC trims those
  constructors and the cctor throws on the first state commit. Rooting the assembly is the fix.
  `Rlp.zkevm.cs` solves the same problem by registering decoders explicitly, but that file is
  selected by `EnableZkEvm`, which also aliases `[ThreadStatic]` to a no-op — not safe for a
  multi-threaded host.

## Layout

```
Nethermind.Evm.Native/   the shared library: [UnmanagedCallersOnly] exports over ITransactionProcessor
  Export.cs              engine lifecycle (one per thread), the two workloads, GC counters
  CallbackState.cs       IWorldStateScopeProvider backed by three Rust function pointers
  ILLink.Descriptors.xml keeps the RLP decoders alive through trimming
TraceHarness/            the same workloads as a plain JIT console app, for dotnet-trace
rust/evmcaller/          loads the .so, installs a reth-style SIGSEGV handler, runs the suite
rust/revmbase/           the same two workloads on revm 38.0.0 (the pin rbuilder uses)
scripts/alloc-report.cs  GCAllocationTick aggregated by allocated type
scripts/alloc-stacks.cs  the same, aggregated by resolved call stack
```

## Signal-handler ordering

`rust/evmcaller` installs a `SA_SIGINFO|SA_ONSTACK` SIGSEGV handler before it touches the runtime,
which is the shape `reth_cli_util::sigsegv_handler::install()` has and the order that works:

- host handler, **then** runtime — the runtime chains to it. Managed null checks become
  `NullReferenceException`, and host faults (stack overflow, wild pointers) still reach the host.
- runtime, **then** host handler — the next managed null dereference kills the process.

Initialise the runtime after every host signal handler is in place, and install none afterwards.

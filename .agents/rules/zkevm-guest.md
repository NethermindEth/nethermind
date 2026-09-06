# zkVM guest (ZisK)

The stateless executor is compiled a second time, from the same sources, into a RISC-V binary that runs
inside the ZisK zkVM: `-p:EnableZkEvm=true`, NativeAOT through `bflat`, no SIMD, no threads, no JIT.
Every file pair `X.std.cs` / `X.zkevm.cs` is that split — `Directory.Build.targets` compiles one or the
other. **Almost all of this repo's code is shared between the two**, so a change made for the guest
lands in the client too, and the guest's fastest form is regularly the host's slowest.

## Step count is 60% of the bill

`ziskemu -X` reports the whole cost model, and steps track only part of it. For one mainnet block:

| bucket | share |
|---|---:|
| MAIN — scales with step count | 60% |
| PRECOMPILES | 16% |
| OPCODES | 13% |
| MEMORY | 10% |

**Keccak is 15% of the bill and almost none of the steps.** One `keccak-f` permutation costs 38,454
units against 15 for an `add`, and it is a syscall that spends a single step. So a change that removes
hashing is worth several times what its step count suggests, and a step-only comparison misses it
entirely — as one round of this work did, reporting −7.74% steps for −6.24% cost. Track TOTAL, and diff
two runs with `--save-stats` / `--ref-stats` rather than by eye.

Keccak volume itself is near-irreducible and has been checked: the guest hashes every witness node to key
it, which *is* the soundness check, and every node it re-encodes on commit, which is the new state root.
Neither is redundant, and there is no double hashing.

## What the prover charges for memory

Measured with `ziskemu -X -S --mem-stats --mem-full-stats`, cost per single access:

| access | cost | | access | cost |
|---|---:|---|---|---:|
| aligned 8B read | **16** | | 2B or 4B read | 122 |
| aligned 8B write | **18** | | 4B write | 193 |
| 1B read | 41 | | 1B *dirty* write | 193 |
| 1B clean write | 66 | | 8B read crossing a word | 191 |
| | | | 8B write crossing a word | 333 |

So the rule is **aligned 8-byte**, not merely "64-bit":

- A byte read (41) is *cheaper* than a 4-byte read (122). Narrowing is not the problem; `int` is.
- An unaligned 8-byte access that crosses a word boundary (191/333) is worse than either, so SWAR over
  unaligned data can lower step count and raise cost at the same time.
- A byte read-modify-write (193) is the single priciest thing you can do to memory.

Memory is ~10% of total prover cost; the rest is dominated by MAIN, which scales with step count. Both
numbers come out of the same run, and they can disagree — see "widening a field" below.

## One thread, and queued work never runs

The guest links `--no-pthread`. Anything that hands work to the thread pool does not run late — it does
not run at all, and the guest **hangs**: enabling the stream interpreter, whose `CodeInfo.GetOrBuildStream`
queues its build with `ThreadPool.UnsafeQueueUserWorkItem`, produced no emulator output whatsoever.
`CodeInfo.AnalyzeInBackgroundIfRequired` is already `#if !ZK_EVM` for exactly this reason. If shared code
you touch schedules, awaits, or lazily initialises through the pool, it needs a synchronous guest arm.

## ILC codegen traps

These are not micro-optimisations; each was worth whole percentage points of the guest.

- **Vector *comparisons* expand to a byte-at-a-time element loop.** ILC spills each 8-byte lane to the
  stack and compares byte by byte, ~11 instructions per byte, so a 32-byte equality costs ~320 steps.
  Never hand-write a `Vector256`/`Vector128` comparison in shared code: use `Bytes.AreEqual32` /
  `Bytes.IsZero32` (a std/zkevm pair), or the value type's own equality if it already carries the split
  (`UInt256.Equals`, `Address.BytesEqual`). Vector *arithmetic* is fine — ILC lowers AND/OR/XOR
  word-wise, and hand-scalarising those measured worse.
- **A 64-bit literal is a five-instruction materialisation at every inlined use.** Put the constants in
  a `static readonly T[]` and load through `MemoryMarshal.GetArrayDataReference`. It has to be an array:
  ILC's preinitialiser constant-folds `static readonly` primitives straight back into materialisation.
- **An explicit static constructor is never preinitialised.** Every static member access of such a type
  then pays a NativeAOT lazy-init check — a lock plus a thread-static lookup. Prefer field initialisers
  or baked constant data.
- **Compound assignment on a memory element blocks address folding.** `bitmap[i] ^= v` captures the
  element address in a temporary (lvalue-once semantics), so the backend cannot fold base+offset into
  both the load and the store. Write `bitmap[i] = bitmap[i] ^ v`.
- **Keep byte and opcode arithmetic 32-bit.** Widening a loaded byte to `nuint` adds an `slli`/`srli`
  zero-extension pair at *every* load, because the JIT does not know `lbu` already zero-extended.
  Widen the walk pointer, not the value.
- **`Array.LongLength` buys nothing.** NativeAOT stores the length as a `uint` field at offset 8; the
  property reads that same 4-byte field and widens it. To stop paying 122 for a length read, stop
  reading it: hoist it into a local, or walk with `ref` + `Unsafe.Add` so the bounds check goes away.
- **`x = default` on a large struct is a call.** A 200-byte `= default` compiles to
  `SpanHelpers.ClearWithoutReferences`, ~63 steps. Initialise only what is not about to be overwritten.

## Widening a field is not enough on its own

The obvious reading of the cost table — make hot `int` fields pointer-wide so their accesses become
8-byte — is not the whole move, and on its own it has measured negative twice:

- `RlpReader.Position` `int` → `nint` backing field: guest −0.34% cost but **+0.17% steps** (the `int`
  property truncates on every read and sign-extends on every write), and on the host
  `RlpTrieNodeEncoding.Encode_Branch` **+18%**. Dropped.
- Carrying already-decoded RLP item lengths in an inline array to avoid re-decoding: **+0.40%** even
  with pointer-wide elements, no bounds checks and no zero-init.

What does work is reducing the *number* of accesses: `RlpReader.SkipItems` walks a run of items with the
cursor in a local (−0.12%), and `EvmStack`'s offset arithmetic moved to `nuint` so the product needs no
extension before it is added to the base (−0.27%).

And where widening does pay, it only pays if the width **survives to the point of use**. Storing
`EvmStack.Head` and `CodeLength` pointer-wide but reading them back through an `int`-typed property gave
−0.29% cost and *+0.29% steps*: the narrowing lets the compiler fold the load back to a four-byte one,
which is equivalent on a little-endian target, so the writes widened and the reads did not — visible in
the disassembly as `sd` and `lw` on the same field. Carrying the offset native-width from the field
through the forty push and pop sites instead gave −0.73% cost *and* −0.04% steps, because it also dropped
the extension each slot address had been paying. A field's declared width is not the win; the absence of
a conversion between it and its use is.

## A shared-code change must pass the host gates

Two of five changes in one round helped the guest and hurt x64. Before merging anything in shared code,
run the benchmark that covers it on master and on the branch:

```bash
dotnet run -c release --project Nethermind.Benchmark.Runner -- --quick --filter '*X*'
```

`--quick` is BenchmarkDotNet's ShortRun; the runner covers both `Nethermind.Benchmark` and
`Nethermind.Evm.Benchmark`.

| touching | run |
|---|---|
| the interpreter, the stack, gas | `*InterpreterLoopBenchmarks*` — 2 cases, the fastest meaningful gate |
| RLP, trie nodes | `*RlpTrieNodeEncodingBenchmark*`, `*RlpDecodeBlockBenchmark*` |
| jumpdest analysis | `*JumpDestinationsBenchmark.Scalar*` — 12 code shapes, ~6 min |
| hashing, comparers | `*Keccak256Benchmarks*`, `*StorageCellBenchmark*`, `*SeqlockCacheBenchmarks*` |

`*EvmStackBenchmarks*` is not a gate — most of its cases measure below a nanosecond.

If the guest form regresses the host, split it: `X.std.cs` keeps what the host had, `X.zkevm.cs` gets
the guest form. Keep the signature identical across the pair and swap only the body — every existing
split does, and a public member whose *type* differed per flavour would be the first place the two
builds' API surfaces diverge.

## Measuring the guest

Build the guest and run one mainnet block through the emulator, then compare step counts. Two rules:

- **Always check the output, not just the step count.** The printed value decodes as
  `StatelessValidationResult`: 32-byte root, then `IsSuccess`, `ChainId`, `SchemaId`. A build that
  silently ran the std-flavour assemblies produces a *matching root* with `IsSuccess = 0x00` and a far
  lower step count.
- **Never build the std flavour in the same worktree.** `artifacts/bin/<project>/release/` is not keyed
  on `EnableZkEvm`, so an earlier `dotnet build`/`dotnet test` leaves assemblies the guest link happily
  reuses. Keep a worktree that has only ever built with `EnableZkEvm=true`, and run host tests and
  benchmarks somewhere else.

For a profile, one emulator run gives everything:

```bash
ziskemu -e nethermind -i <block>.ssz -X -S --no-thousands-sep \
        --mem-stats --mem-full-stats -T 60 -H 40 --disasm out.disasm
```

Add `--save-stats <file>` to snapshot that report, and `--ref-stats <file>` on a later run to print the
two cost distributions side by side — worth more than comparing step counts by eye, since the buckets
move independently.

`-X` (capital) is the real report; lowercase `-x` is a legacy stub. `--disasm` writes an objdump-style
listing with per-instruction execution counts — resolve those addresses against the ELF symbol table
rather than trusting the listing's own headers, since ILC emits methods without symbols and their code
is otherwise charged to whatever came before.

## The toolchain leaves more on the table than the source does

`bflat build` takes `-m zba` and `-m zbb`, both accepted, and ILC really does emit the fused forms:
`zba`'s `sh1add.uw` / `sh2add.uw` / `sh3add.uw` collapse the zero-extend, scale and add that every
`int`-indexed access pays, and `zbb`'s `rev8` replaces an eleven-instruction byte swap with one
instruction. ZisK cannot take either yet — `-m zba` dies in the transpiler with `found invalid
riscv_instruction.inst_name=sh2add.uw`, while `-m zbb` transpiles *without complaint* and then reads a
garbage address, which is a silent mistranslation rather than a refusal. Together they are worth more
than everything achieved in the guest source so far. Neither is actionable from this repo; both are worth
raising upstream, the `zbb` one as a correctness bug in its own right.

Every `-X` run also prints a `dma_xmemcmp` deprecation notice, so the pinned bflat/ziskos image is behind
the emulator it runs against.

## Measured and rejected — don't retry these

| idea | result |
|---|---|
| `Accelerators.Keccak256` (the zkVM's whole-message keccak) instead of the managed sponge over `KeccakF` | **+12.05%** — it is a software sponge; the permutation syscall plus our own absorb wins |
| SWAR word-skip in the jumpdest scan | +2.22% — only 1.39M of 2.098M visited bytes are outside `[JUMPDEST, PUSH32]` and they come in runs of ~2, and `TrailingZeroCount` is a software fallback (no Zbb) |
| jumpdest scan's moving reference as *shared* code | host +40…55% on PUSH-heavy shapes; ships as a split |
| `RlpReader.Position` as `nint` | see above — host +18% |
| carrying branch-RLP child lengths between the two passes | +0.40% / +0.45% |
| hand-scalarised `Vector256` AND/OR/XOR/NOT | +0.31% — ILC's own expansion is tighter |
| forcing the stream interpreter on the guest | +76%, and it **hangs** unless the build is also made synchronous — see "one thread" above. The per-block gas precharge is real, but the `StreamOp[]` build is ~16 bytes per code byte |
| `OpcodeResult` struct-returning dispatch | +1.3% |
| per-node child offset cache in `TrieNode.SeekChildNotNull` | +3.6% — nodes are sought about once |
| a "hash-and-forget" trie over the witness blobs, skipping `TrieNode` | not built: `DecodeRlp` already runs exactly once per node, from one call site, so there is no redundant object work to remove |
| a little-endian guest stack, to drop the byte swaps from arithmetic | not built: the byte order is not confined to a conversion — `Push2Bytes`…`Push32Bytes`, `PushAddress`, `PopAddress`, `PeekWord256` each encode it. Ordering the words in place for LT/GT/SLT/SGT took the same prize without a second representation |
| lazy jumpdest scanning, extending the bitmap only as far as each jump needs | +0.11% — jump targets reach nearly the end of real bytecode, so there is no unscanned tail to save |
| eliminating array bounds checks for their length reads | not worth a campaign: length reads are 18% of four-byte reads, ~0.4% of total cost, and most sit in corelib |
| a custom open-addressed map/set keyed on the existing 64-bit `IHash64bit` hash | +0.05…0.09% across three targets — these collections are small, so probe chains are short, and `Dictionary`'s `int[]` buckets index with a shift where a wide entry needs a multiply |

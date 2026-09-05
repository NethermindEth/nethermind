# zkVM guest (ZisK)

The stateless executor is compiled a second time, from the same sources, into a RISC-V binary that runs
inside the ZisK zkVM: `-p:EnableZkEvm=true`, NativeAOT through `bflat`, no SIMD, no threads, no JIT.
Every file pair `X.std.cs` / `X.zkevm.cs` is that split — `Directory.Build.targets` compiles one or the
other. **Almost all of this repo's code is shared between the two**, so a change made for the guest
lands in the client too, and the guest's fastest form is regularly the host's slowest.

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

## Widening a field is usually the wrong fix

The obvious reading of the cost table — make hot `int` fields pointer-wide so their accesses become
8-byte — has been measured twice and is not recommended:

- `RlpReader.Position` `int` → `nint` backing field: guest −0.34% cost but **+0.17% steps** (the `int`
  property truncates on every read and sign-extends on every write), and on the host
  `RlpTrieNodeEncoding.Encode_Branch` **+18%**. Dropped.
- Carrying already-decoded RLP item lengths in an inline array to avoid re-decoding: **+0.40%** even
  with pointer-wide elements, no bounds checks and no zero-init.

What does work is reducing the *number* of accesses: `RlpReader.SkipItems` walks a run of items with the
cursor in a local (−0.12%), and `EvmStack`'s offset arithmetic moved to `nuint` so the product needs no
extension before it is added to the base (−0.27%).

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

`-X` (capital) is the real report; lowercase `-x` is a legacy stub. `--disasm` writes an objdump-style
listing with per-instruction execution counts — resolve those addresses against the ELF symbol table
rather than trusting the listing's own headers, since ILC emits methods without symbols and their code
is otherwise charged to whatever came before.

## Measured and rejected — don't retry these

| idea | result |
|---|---|
| `Accelerators.Keccak256` (the zkVM's whole-message keccak) instead of the managed sponge over `KeccakF` | **+12.05%** — it is a software sponge; the permutation syscall plus our own absorb wins |
| SWAR word-skip in the jumpdest scan | +2.22% — only 1.39M of 2.098M visited bytes are outside `[JUMPDEST, PUSH32]` and they come in runs of ~2, and `TrailingZeroCount` is a software fallback (no Zbb) |
| jumpdest scan's moving reference as *shared* code | host +40…55% on PUSH-heavy shapes; ships as a split |
| `RlpReader.Position` as `nint` | see above — host +18% |
| carrying branch-RLP child lengths between the two passes | +0.40% / +0.45% |
| hand-scalarised `Vector256` AND/OR/XOR/NOT | +0.31% — ILC's own expansion is tighter |
| forcing the stream interpreter on the guest | +76% |
| `OpcodeResult` struct-returning dispatch | +1.3% |
| per-node child offset cache in `TrieNode.SeekChildNotNull` | +3.6% — nodes are sought about once |

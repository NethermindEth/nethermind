# JitAsm

A tool for viewing JIT-compiled assembly output for .NET methods. Useful for analyzing code generation, verifying optimizations, and comparing different implementations.

## How It Works

JitAsm spawns a child process with JIT diagnostic environment variables (`DOTNET_JitDisasm`) to capture disassembly output.

By default, the tool simulates **Tier-1 recompilation with Dynamic PGO** — the same compilation tier that runs in production after warm-up. This produces the most representative assembly: smaller code, eliminated static base helpers, and PGO-guided branch layout.

### Compilation Tiers

The .NET runtime compiles methods through multiple stages:

| Stage | Description | Typical Size |
|-------|-------------|-------------|
| **Tier-0** | Quick JIT, minimal optimization (`minopt`) | Largest |
| **Instrumented Tier-0** | Tier-0 + PGO probes for profiling | Larger still |
| **Tier-1 + PGO** (default) | Full optimization with profile data | **Smallest** |
| **FullOpts** (`--fullopts`) | Full optimization, no PGO, no tiering | Middle |

Key difference between Tier-1 and FullOpts:
- **FullOpts** (`DOTNET_TieredCompilation=0`) compiles in a single pass. Cross-module static base helpers (`CORINFO_HELP_GET_NONGCSTATIC_BASE`) persist because the JIT hasn't resolved the static base address yet.
- **Tier-1 + PGO** compiles after Tier-0 has executed. By then, static bases are resolved, and the JIT can embed addresses directly — eliminating the helper entirely. PGO data also improves branch layout and inlining decisions.

### Two-Pass Static Constructor Handling

When a method references static fields, the JIT may include static constructor initialization checks (`CORINFO_HELP_GET_NONGCSTATIC_BASE`, `CORINFO_HELP_GETSHARED_NONGCSTATIC_BASE`). JitAsm automatically detects these and runs a second pass with static constructors pre-initialized, showing the steady-state optimized code path.

In Tier-1 mode (default), many of these helpers are eliminated naturally since the static bases are already resolved by Tier-0 execution.

### Tier-1 Simulation

The tool drives through all PGO compilation stages:

1. **Invoke** method via reflection (triggers Tier-0 JIT + installs call counting stubs)
2. **Wait** for Instrumented Tier-0 recompilation (PGO profiling version)
3. **Invoke** again (triggers call counter through instrumented code)
4. **Wait** for Tier-1 recompilation (fully optimized with PGO data)
5. **Capture** the final Tier-1 assembly output

Critical env var: `DOTNET_TC_CallCountingDelayMs=0` — without this, counting stubs aren't installed in time and Tier-1 never triggers.

## Usage

```bash
dotnet run --project tools/JitAsm -c Release -- [options]
```

### Options

| Option | Description |
|--------|-------------|
| `-a, --assembly <path>` | Path to the assembly containing the method (required) |
| `-t, --type <name>` | Fully qualified type name (optional, searches all types if not specified) |
| `-m, --method <name>` | Method name to disassemble (required) |
| `--type-params <types>` | Method generic type parameters (comma-separated) |
| `--class-type-params <types>` | Class generic type parameters (comma-separated, for generic containing types) |
| `--fullopts` | Use single-pass FullOpts compilation (`TieredCompilation=0`) instead of Tier-1 + PGO |
| `--no-annotate` | Disable per-instruction annotations (throughput, latency, uops, ports). Annotations are **on by default** |
| `--arch <arch>` | Target microarchitecture for annotations (default: `zen4`). See [Supported Architectures](#supported-architectures) |
| `--skip-cctor-detection` | Skip automatic static constructor detection (single pass only) |
| `-v, --verbose` | Show resolution details and both passes |

### Default Mode: Tier-1 + PGO

By default, the tool simulates Tier-1 recompilation with Dynamic PGO. This is the compilation tier code runs at in production and produces the most optimized output.

```bash
# Default: Tier-1 + PGO (production-representative)
dotnet run --project tools/JitAsm -c Release -- -a path/to/assembly.dll -m MethodName
```

### FullOpts Mode

Use `--fullopts` when you need the older single-pass compilation. This is faster (no invocation/sleep cycle) but may show static base helpers and lack PGO-guided optimizations.

```bash
# FullOpts: single compilation, no PGO
dotnet run --project tools/JitAsm -c Release -- -a path/to/assembly.dll -m MethodName --fullopts
```

## Prerequisites

Build the target project in Release configuration before disassembling:

```bash
dotnet build src/Nethermind/Nethermind.Evm -c Release
```

Assemblies are output to `src/Nethermind/artifacts/bin/{ProjectName}/release/{ProjectName}.dll`. For example:
- `src/Nethermind/artifacts/bin/Nethermind.Evm/release/Nethermind.Evm.dll`
- `src/Nethermind/artifacts/bin/Nethermind.Core/release/Nethermind.Core.dll`

## Platform Notes (Windows)

On Windows, follow these rules to avoid argument parsing errors:

- **Avoid quoted strings** around paths and type parameters — they can cause `'' was not matched` errors
- **Use forward slashes** in paths (works on both Windows and Linux)
- **Put the entire command on a single line** — avoid line continuations (`\`)
- Use absolute paths when possible for clarity

```bash
# Good (Windows)
dotnet run --project tools/JitAsm -c Release -- -a D:/GitHub/nethermind/src/Nethermind/artifacts/bin/Nethermind.Evm/release/Nethermind.Evm.dll -t Nethermind.Evm.EvmStack -m PushBytesRef

# Bad (may fail on Windows)
dotnet run --project tools/JitAsm -c Release -- \
    -a "src/Nethermind/artifacts/bin/Nethermind.Evm/release/Nethermind.Evm.dll" \
    -t "Nethermind.Evm.EvmStack" \
    -m PushBytesRef
```

On Linux/macOS, both styles work fine.

## Examples

### Basic Usage

```bash
# Disassemble a simple method (searches all types, default Tier-1 + PGO)
dotnet run --project tools/JitAsm -c Release -- -a src/Nethermind/artifacts/bin/Nethermind.Evm/release/Nethermind.Evm.dll -m PushBytesRef
```

### With Type Specification

```bash
# Specify the containing type to narrow the search
dotnet run --project tools/JitAsm -c Release -- -a src/Nethermind/artifacts/bin/Nethermind.Evm/release/Nethermind.Evm.dll -t Nethermind.Evm.EvmStack -m PushBytesRef
```

### Generic Methods (Method-Level Type Parameters)

Use `--type-params` for type parameters on the method itself:

```bash
# PushBytes<TTracingInst>(...) where TTracingInst = OffFlag
dotnet run --project tools/JitAsm -c Release -- -a src/Nethermind/artifacts/bin/Nethermind.Evm/release/Nethermind.Evm.dll -t Nethermind.Evm.EvmStack -m PushBytes --type-params Nethermind.Core.OffFlag
```

### Generic Classes (Class-Level Type Parameters)

Use `--class-type-params` when the containing class is generic. This is separate from `--type-params` which is for method-level generics. Generic type names can omit the backtick arity suffix (e.g., `TransactionProcessorBase` matches `TransactionProcessorBase`1`).

```bash
# TransactionProcessorBase<EthereumGasPolicy>.Execute<OffFlag>(...)
dotnet run --project tools/JitAsm -c Release -- -a src/Nethermind/artifacts/bin/Nethermind.Evm/release/Nethermind.Evm.dll -t Nethermind.Evm.TransactionProcessing.TransactionProcessorBase -m Execute --class-type-params Nethermind.Evm.GasPolicy.EthereumGasPolicy --type-params Nethermind.Core.OffFlag
```

### Multiple Generic Parameters

Use comma-separated type names (no spaces after commas):

```bash
dotnet run --project tools/JitAsm -c Release -- -a src/Nethermind/artifacts/bin/Nethermind.Evm/release/Nethermind.Evm.dll -m SomeGenericMethod --type-params System.Int32,System.String
```

### EVM Instruction Example

Disassemble the MUL opcode implementation with specific gas policy and tracing flags:

```bash
dotnet run --project tools/JitAsm -c Release -- -a src/Nethermind/artifacts/bin/Nethermind.Evm/release/Nethermind.Evm.dll -t Nethermind.Evm.EvmInstructions -m InstructionMath2Param --type-params Nethermind.Evm.GasPolicy.EthereumGasPolicy,Nethermind.Evm.EvmInstructions+OpMul,Nethermind.Core.OffFlag
```

This shows the JIT output for `InstructionMath2Param<EthereumGasPolicy, OpMul, OffFlag>`:
- `EthereumGasPolicy` - Standard Ethereum gas accounting
- `OpMul` - The multiplication operation (nested type, use `+` syntax)
- `OffFlag` - Tracing disabled (allows dead code elimination of tracing paths)

Other math operations can be viewed by replacing `OpMul` with: `OpAdd`, `OpSub`, `OpDiv`, `OpMod`, `OpSDiv`, `OpSMod`, `OpLt`, `OpGt`, `OpSLt`, `OpSGt`, `OpEq`, `OpAnd`, `OpOr`, `OpXor`.

### Type Aliases

Common C# type aliases are supported:

```bash
# These are equivalent
--type-params int
--type-params System.Int32
```

Supported aliases: `bool`, `byte`, `sbyte`, `char`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `float`, `double`, `decimal`, `string`, `object`, `nint`, `nuint`

### Verbose Mode

```bash
# Show detailed output including cctor detection and compilation tier info
dotnet run --project tools/JitAsm -c Release -- -a src/Nethermind/artifacts/bin/Nethermind.Evm/release/Nethermind.Evm.dll -m PushBytesRef -v
```

### Skip Static Constructor Detection

```bash
# Single pass only (faster, but may show cctor overhead)
dotnet run --project tools/JitAsm -c Release -- -a src/Nethermind/artifacts/bin/Nethermind.Evm/release/Nethermind.Evm.dll -m SomeMethod --skip-cctor-detection
```

### FullOpts vs Tier-1 Comparison

Compare the same method under different compilation modes to see what Tier-1 + PGO eliminates:

```bash
# Tier-1 + PGO (default) — production-representative
dotnet run --project tools/JitAsm -c Release -- -a src/Nethermind/artifacts/bin/Nethermind.Evm/release/Nethermind.Evm.dll -t Nethermind.Evm.BlockExecutionContext -m GetBlobBaseFee > tier1.asm 2>&1

# FullOpts — single-pass, no PGO
dotnet run --project tools/JitAsm -c Release -- -a src/Nethermind/artifacts/bin/Nethermind.Evm/release/Nethermind.Evm.dll -t Nethermind.Evm.BlockExecutionContext -m GetBlobBaseFee --fullopts > fullopts.asm 2>&1

# Compare
diff tier1.asm fullopts.asm
```

Example result for `GetBlobBaseFee`:
- **FullOpts**: 356 bytes, has `CORINFO_HELP_GET_NONGCSTATIC_BASE`
- **Tier-1 + PGO**: 236 bytes (34% smaller), helper eliminated entirely

## Example Output

Default Tier-1 + PGO output:

```
; Assembly listing for method Namespace.Type:Method() (Tier1)
; Emitting BLENDED_CODE for generic X64 + VEX + EVEX on Windows
; Tier1 code                 ← Tier-1 recompilation (production tier)
; optimized code
; optimized using Dynamic PGO
; rsp based frame
; partially interruptible
; with Dynamic PGO: fgCalledCount is 50

G_M000_IG01:                ;; offset=0x0000
       sub      rsp, 40
       ...

; Total bytes of code 236
```

FullOpts output (with `--fullopts`):

```
; Assembly listing for method Namespace.Type:Method() (FullOpts)
; FullOpts code              ← Single-pass FullOpts
; optimized code
; No PGO data                ← No profile data available
       ...

; Total bytes of code 356
```

## Instruction Annotations

Per-instruction performance data from [uops.info](https://uops.info) is included **by default**, showing throughput, latency, micro-op count, and execution port usage inline with the assembly output. Use `--no-annotate` to disable.

### Setup

Download the uops.info instruction database (110MB, one-time):

```bash
curl -o tools/JitAsm/instructions.xml https://uops.info/instructions.xml
```

On first use, the XML is preprocessed into a compact binary cache (`instructions.db`, ~1-2MB). Subsequent runs load from the cache.

### Usage

```bash
# Annotations are on by default (zen4)
dotnet run --project tools/JitAsm -c Release -- -a path/to/assembly.dll -m MethodName

# Use a different architecture
dotnet run --project tools/JitAsm -c Release -- -a path/to/assembly.dll -m MethodName --arch alder-lake

# Disable annotations
dotnet run --project tools/JitAsm -c Release -- -a path/to/assembly.dll -m MethodName --no-annotate
```

### Annotation Format

Each instruction line gets an end-of-line annotation:

```
       add      rax, rcx                 ; [TP:0.25 | Lat: 1 | Uops:1]
       mov      qword ptr [rbp+10h], rax ; [TP:0.50 | Lat:11 | Uops:1]
       vmovdqu  ymm0, ymmword ptr [rsi]  ; [TP:0.50 | Lat: 8 | Uops:1 | 1*FP_LD]
```

| Field | Meaning |
|-------|---------|
| `TP` | Reciprocal throughput (cycles per instruction). Lower = faster. |
| `Lat` | Latency (cycles from input ready to output ready). Matters for dependency chains. |
| `Uops` | Micro-op count. Fewer = less pressure on the execution engine. |
| Ports | Execution port usage (e.g., `1*p0156`). Shows which functional units are used. |

### Supported Architectures

| `--arch` value | CPU | Description |
|----------------|-----|-------------|
| `zen4` (default) | AMD Zen 4 | Ryzen 7000 / EPYC 9004 |
| `zen3` | AMD Zen 3 | Ryzen 5000 / EPYC 7003 |
| `zen2` | AMD Zen 2 | Ryzen 3000 / EPYC 7002 |
| `alder-lake` | Intel ADL-P | 12th Gen Core (P-cores) |
| `rocket-lake` | Intel RKL | 11th Gen Core |
| `ice-lake` | Intel ICL | 10th Gen Core |
| `tiger-lake` | Intel TGL | 11th Gen Mobile |
| `skylake` | Intel SKL | 6th Gen Core |

When data for the selected architecture is unavailable for a specific instruction, the tool falls back to a nearby architecture in the same family.

### Annotation Details

**Mnemonic mapping:** The .NET JIT uses Intel-style mnemonic aliases (e.g., `je`, `jne`, `ja`) while uops.info uses canonical forms (`jz`, `jnz`, `jnbe`). The annotator automatically maps between them for conditional jumps, conditional moves (`cmove`→`cmovz`), and set-byte instructions (`sete`→`setz`).

**LEA handling:** The `lea` instruction computes an address but doesn't access memory. Its second operand is classified as `agen` (address generation) rather than a memory load, matching the uops.info encoding.

**.NET JIT-specific prefixes:**
- `gword ptr` — GC-tracked pointer-width memory reference. Treated as `m64` on x64.
- `bword ptr` — Pointer-width memory reference without GC tracking. Treated as `m64` on x64.
- `SHORT` / `NEAR` — Jump distance hints from the JIT. Stripped before operand classification.

**Skipped instructions:** `call`, `ret`, `int3`, `nop`, and `int` are not annotated. `ret` is skipped because uops.info's `TP_unrolled` for `ret` reflects return stack buffer mispredictions in microbenchmarks, not real-world performance.

## Iterative Workflow

JitAsm is designed for rapid iteration when optimizing code:

1. **Modify source code**
2. **Build the target assembly:**
   ```bash
   dotnet build src/Nethermind/Nethermind.Evm -c Release
   ```
3. **View the generated assembly:**
   ```bash
   dotnet run --project tools/JitAsm -c Release -- -a src/Nethermind/artifacts/bin/Nethermind.Evm/release/Nethermind.Evm.dll -m YourMethod
   ```
4. **Compare output between iterations**

For fast iteration during optimization, `--fullopts` skips the invocation/sleep cycle (~4s faster) at the cost of less representative output. Use default Tier-1 for final verification.

## Environment Variables

The tool sets these JIT diagnostic variables for the child process:

### Default Mode (Tier-1 + PGO)

| Variable | Value | Purpose |
|----------|-------|---------|
| `DOTNET_TieredCompilation` | `1` | Enable tiered compilation |
| `DOTNET_TieredPGO` | `1` | Enable Dynamic PGO |
| `DOTNET_TC_CallCountThreshold` | `1` | Minimal call count before recompilation |
| `DOTNET_TC_CallCountingDelayMs` | `0` | Install counting stubs immediately (critical) |
| `DOTNET_JitDisasm` | `<method>` | Output disassembly for matching methods |
| `DOTNET_JitDiffableDasm` | `1` | Consistent, diffable output format |

### FullOpts Mode (`--fullopts`)

| Variable | Value | Purpose |
|----------|-------|---------|
| `DOTNET_TieredCompilation` | `0` | Disable tiered compilation |
| `DOTNET_TC_QuickJit` | `0` | Disable quick JIT |
| `DOTNET_JitDisasm` | `<method>` | Output disassembly for matching methods |
| `DOTNET_JitDiffableDasm` | `1` | Consistent, diffable output format |

## Comparing Generic Specializations (OffFlag vs OnFlag)

A common workflow is comparing two generic instantiations to verify dead code elimination. Nethermind uses the `IFlag` pattern (`OnFlag`/`OffFlag`) for compile-time branch elimination.

```bash
# Generate ASM for tracing disabled (common case)
dotnet run --project tools/JitAsm -c Release -- -a src/Nethermind/artifacts/bin/Nethermind.Evm/release/Nethermind.Evm.dll -t Nethermind.Evm.EvmInstructions -m InstructionMath2Param --type-params Nethermind.Evm.GasPolicy.EthereumGasPolicy,Nethermind.Evm.EvmInstructions+OpAdd,Nethermind.Core.OffFlag > off.asm 2>&1

# Generate ASM for tracing enabled
dotnet run --project tools/JitAsm -c Release -- -a src/Nethermind/artifacts/bin/Nethermind.Evm/release/Nethermind.Evm.dll -t Nethermind.Evm.EvmInstructions -m InstructionMath2Param --type-params Nethermind.Evm.GasPolicy.EthereumGasPolicy,Nethermind.Evm.EvmInstructions+OpAdd,Nethermind.Core.OnFlag > on.asm 2>&1

# Compare: the OnFlag version should have tracing code that is absent from OffFlag
diff off.asm on.asm
```

**Red flag:** If both versions have identical code size, the JIT may not be eliminating dead code as expected.

## Extracting Metrics from ASM Output

Use these patterns to extract key metrics from the disassembly output for tracking optimization progress:

```bash
# Code size (from the last line, e.g., "; Total bytes of code 40")
tail -1 output.asm

# Basic block count (fewer = simpler control flow = better branch prediction)
grep -c "G_M000_IG[0-9]*:" output.asm

# Branch count (conditional jumps)
grep -cE "\bj(e|ne|g|l|ge|le|a|b|ae|be|nz|z)\b" output.asm

# Call count (should be minimal in hot paths)
grep -c "call" output.asm

# Register saves in prologue (>4 push = register pressure issue)
grep -c "push" output.asm

# Stack frame size (from prologue, e.g., "sub rsp, 40")
grep "sub.*rsp" output.asm
```

## Reading Assembly Output

### Output Structure

The disassembly header contains useful metadata:

```
; Assembly listing for method Namespace.Type:Method() (Tier1)
; Emitting BLENDED_CODE for generic X64 + VEX + EVEX on Windows
; Tier1 code                 ← Tier-1 optimized (production tier)
; optimized code
; optimized using Dynamic PGO ← PGO-guided optimizations applied
; rsp based frame
; partially interruptible
; with Dynamic PGO: fgCalledCount is 50  ← How many times method was called during profiling
```

The code is organized into **basic blocks** labeled `G_M000_IG01`, `G_M000_IG02`, etc. Each block is a straight-line sequence of instructions ending with a branch or fall-through.

### Common Red Flags

| Pattern in ASM | Meaning | Severity |
|----------------|---------|----------|
| `call CORINFO_HELP_BOX` | Boxing allocation in hot path | High |
| `call CORINFO_HELP_VIRTUAL_FUNC_PTR` | Virtual dispatch not devirtualized (~25-50 cycles) | High |
| `call CORINFO_HELP_GETSHARED_NONGCSTATIC_BASE` | Static constructor check | High |
| `call CORINFO_HELP_GET_NONGCSTATIC_BASE` | Cross-module static base resolution (eliminated in Tier-1) | Medium* |
| `call CORINFO_HELP_ASSIGN_REF` | GC write barrier (bad in loops) | High |
| `callvirt [Interface:Method()]` | Interface dispatch not devirtualized | High |
| `call [SmallType:SmallMethod()]` | Failed inlining of small method | Medium |
| `cmp ...; jae THROW_LABEL` | Bounds check (may be eliminable) | Medium |
| `idiv` | Division by non-constant (~20-80 cycles) | Medium |
| `>4 push` instructions in prologue | Register pressure | Medium |
| `vmovdqu32 zmmword ptr` in prologue | Large stack zeroing (cold locals bloating hot path) | Low |

*`CORINFO_HELP_GET_NONGCSTATIC_BASE` appears in FullOpts mode for cross-module static field access but is naturally eliminated in Tier-1 + PGO. If you see it in default (Tier-1) output, it indicates the static base couldn't be resolved at runtime — a real issue.

### What Good ASM Looks Like

- Few basic blocks (simple control flow)
- No `call` instructions in the hot path (everything inlined)
- Compact code size (better I-cache utilization)
- No redundant loads of the same memory location
- SIMD instructions (`vpaddd`, `vpxor`, etc.) for bulk data operations
- `cmov*` (conditional moves) instead of branches where appropriate
- Fall-through on the hot path, forward jumps to cold/error paths
- `; optimized using Dynamic PGO` in the header (confirms PGO applied)

## Troubleshooting

### No disassembly output

- Verify the assembly path is correct and the DLL exists
- Check if the method name is spelled correctly (case-sensitive)
- Try without the `-t` option to search all types
- Use `-v` for verbose output to see what's happening
- Ensure you built with `-c Release` (Debug builds produce different code)

### Tier-1 produces no output / shows Tier-0 instead

- The Tier-1 simulation invokes the method via reflection with default arguments. Methods taking **ref struct** parameters (`Span<T>`, `ReadOnlySpan<T>`, `Memory<T>`) cannot be invoked this way — the simulation silently fails and produces no output.
- **Workaround:** Use `--fullopts` for methods with ref struct parameters. It compiles in a single pass without invocation.
- For other methods: this means the Tier-1 recompilation didn't fire. The method may not be invocable with null/default arguments.
- Check `-v` output for error messages during invocation phases.

### Method not found

- Ensure the assembly is built (Release configuration recommended)
- For generic methods, provide all required type parameters via `--type-params`
- For methods in generic classes, provide class type parameters via `--class-type-params`
- Use fully qualified type names for type parameters from other assemblies
- Generic type names can omit the backtick arity (e.g., `TransactionProcessorBase` matches `TransactionProcessorBase`1`)

### Multiple methods with same name

- Specify the type with `-t` to narrow the search
- The tool will use the first matching overload if multiple exist

### On Windows: `'' was not matched` error

- Remove all quotes from arguments — pass paths and type names unquoted
- Replace backslashes with forward slashes in paths
- Put the entire command on a single line

## Running Tests

```bash
dotnet test --project tools/JitAsm.Test -c Release
```

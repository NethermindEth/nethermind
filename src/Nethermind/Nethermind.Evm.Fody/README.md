# Opcode naming

This build-only Fody weaver gives every opcode a distinct `Op…` method
name in CPU profiles, even when the profiler omits generic arguments.
The EVM project runs it after InlineIL on ordinary builds.

The generic shape is part of the execution design. Struct type arguments select
the opcode body, gas policy, tracing, cancellation and fork behavior when the
dispatch table is constructed. They give the JIT/AOT compiler concrete targets
for static interface calls and constants for flag checks, allowing it to inline
instruction work and eliminate disabled paths. One source template can therefore
serve many specialized handlers without repeating dispatch logic for each opcode.
Retaining those arguments and constraints preserves these optimization opportunities.

The profiling side effect is that different native specializations still have
the same metadata method name, `ExecuteOpcode`. Profilers that omit generic
arguments show many indistinguishable entries under that name. When instruction
bodies inline, their separate frames disappear too, making it difficult to tell
which opcode accounts for the samples or to compare an opcode across runs.

The renaming step gives those dispatch entry points stable names such as
`OpSLoad`, `OpPush2` and `OpStaticCall` while keeping their generic shape. This
makes opcode attribution visible even without generic arguments in the profile;
tracing, cancellation and fork variants of the same opcode still share its name.
Generating the named bodies after compilation keeps the source template shared
and avoids relying on a forwarding wrapper being inlined to retain the dispatch
shape.

It clones `ExecuteOpcode` (or the dedicated JUMPI dispatcher) and its pointer factory for each opcode,
then redirects the table construction calls to the named factories. The cloned
methods retain all generic arguments, constraints, implementation flags and the
explicit `tail.calli`. The instruction implementation stays in one source
template; there is no runtime forwarding wrapper. Table refreshes use the same
rewritten construction paths.

CALL, CALLCODE, DELEGATECALL and STATICCALL have separate names, as do CREATE
and CREATE2. Their fork-selection factory chains are cloned with the opcode name
carried through to the final function pointer. Gas-policy and fork variants keep
their generic specialization. The build fails if the generated names do not cover
the instruction enum; unassigned table entries use `OpBadInstruction`.

After checking for remaining references, the weaver removes the original pointer
factories. The two dispatch templates remain for the reflection tests that compare
their IL and attributes against every named handler. Invalid debug sequence points,
scope boundaries and local indices produce warnings and omit the affected debug
information; invalid executable signatures still fail the build.

Validation:

```sh
dotnet build src/Nethermind/Nethermind.Evm.Test/Nethermind.Evm.Test.csproj -c Release -nr:false
dotnet test --project src/Nethermind/Nethermind.Evm.Test/Nethermind.Evm.Test.csproj -c Release --no-build -- --filter "FullyQualifiedName~VirtualMachineTests|FullyQualifiedName~OpcodeWeaverTests"
```

When editing the weaver itself, disable MSBuild node reuse as above so a node
cannot retain a previously loaded version of the add-in.

For native-code inspection, set `DOTNET_JitDisasm` to
`*:OpPush1 *:OpPush2 *:OpAdd *:OpSLoad` and
`DOTNET_JitStdOutFile` to a local output file before running the tests.
Confirm that the named methods contain the instruction work and tail transfers,
not calls to `ExecuteOpcode`. Compare unprofiled timings separately before
assuming unchanged throughput; identical IL semantics do not guarantee identical
JIT layout or tiering behavior.

## Measured scope

A Windows x64 Release build on SDK 10.0.400 produces a 487,424-byte managed
`Nethermind.Evm.dll`. Named handler bodies and their specialized factories add IL
and metadata even after unused factories are removed. Managed assembly size is
separate from native JIT code size and the guest ELF measurements below.

Windows x64 .NET 10 FullOpts disassembly confirmed that the SLOAD, metered
SSTORE and STATICCALL instruction bodies inline into their named handlers while
retaining tail dispatch. This does not establish a block-processing speedup or
total JIT code size across all generic specializations. `SkipLocalsInit` on
local-free opcode bodies records the call-chain policy but changes no emitted
local-initialization flag for those methods.

For the naming and annotation changes relative to `014462bff6`, the pinned
bflat/ZisK Docker toolchain in the guest Makefile produced these results on
the existing block 25,532,382 witness:

| Metric | Parent | Naming and annotations |
|---|---:|---:|
| Stripped ELF bytes | 4,651,736 | 4,651,528 |
| `.text` bytes | 3,271,696 | 3,270,464 |
| Executed steps | 396,212,620 | 396,802,794 |
| Modeled memory cost | 4,076,629,105 | 4,084,009,643 |
| Modeled total cost | 45,580,415,805 | 45,635,335,086 |

Both guests validated the block with identical output. Naming alone had identical
step counts and costs to the parent; the full annotation change increases steps
by 0.149% and modeled total cost by 0.120% on this witness despite reducing code
size. These are single-witness guest measurements, not proving time or desktop
JIT performance measurements.

# Pure-word opcode extractor scope

This tool admits a pinned source profile for the 26 Amsterdam pure-word
instructions `ADD` through `CLZ`. It is a Roslyn extractor and Lean emitter; it
is not a replacement C# arithmetic kernel.

## Exact source admission

The source profile accepts a revision only when all of these checks pass:

1. Each of the 40 C# source inputs has its pinned,
   complete non-layout Roslyn token/trivia fingerprint. The fingerprint includes
   every active token and every comment, directive, and disabled-text trivia.
   `src/Nethermind/Directory.Build.targets` is admitted separately by its exact
   raw hash and checked standard-versus-zkEVM source-selection clauses.
   Consequently an unmodeled statement, branch, early return, preprocessor
   branch, semantic expression, or build-selection change fails closed.
   Regenerating the JSON and Lean outputs cannot change these fingerprints.
2. Roslyn AST checks find exactly one assignment to each selected instruction
   in `GenerateOpcodeHandlers`. Ordinary assignments must be direct top-level
   statements; shifts must be directly under `spec.ShiftOpcodesEnabled`, and
   `CLZ` directly under `spec.CLZEnabled`. A duplicate, later competing
   assignment, or dead-branch copy is rejected.
3. The handler body is read from that unique assignment. Its tracing and
   cancellation parameters must flow directly into `OpcodeHandler`, whose
   function pointer must target
   `ExecuteOpcode<TOpcode,TTracingInst,TCancelable,OnFlag>`.
4. The four live `OpcodeTable` fields and their flag selection are checked:
   `NoTrace`, `NoTraceCancelable`, `Traced`, and `TracedCancelable`. The profile
   also admits the standard `GetOpcodeTable` cache factory and refresh method,
   and checks the mainnet transaction-processing trace selection, cancellation
   selection, table preparation, dispatch-loop entry, and both function-pointer
   invocation paths before emitting the 104 closed roots.
5. `NamedReleaseSpec` must replay every ancestor from root to leaf, and its
   generic singleton must construct the selected fork type. Every parent edge
   from Spurious Dragon through Amsterdam is admitted, so EIP-160, EIP-145, and
   EIP-7939 cannot be skipped by changing an intermediate parent.
6. Instruction bytes are parsed from the production `Instruction : byte` enum.
   Missing, duplicate, or values outside `0..255` are rejected. Wrapper arity,
   gas tags and constants, Amsterdam activation inheritance, stack layout, and
   the admitted instruction/core routes are checked before emission.

The pinned closure currently contains 40 production C# files plus the raw
`Directory.Build.targets` source-selection file: gas constants and policy,
flags and fork inheritance, instruction enum and implementations, stack,
virtual-machine dispatch and opcode bodies, the mainnet transaction tracing
selection, and the block-processing registration of
`EthereumVirtualMachine : VirtualMachine<EthereumGasPolicy>`.

```text
src/Nethermind/Nethermind.Core/GasCostOf.cs
src/Nethermind/Nethermind.Core/Specs/IReleaseSpecExtensions.cs
src/Nethermind/Nethermind.Core/TypeFlags.cs
src/Nethermind/Nethermind.Evm/EvmStack.cs
src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs
src/Nethermind/Nethermind.Evm/GasPolicy/IGasCost.cs
src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs
src/Nethermind/Nethermind.Evm/Instruction.cs
src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Bitwise.cs
src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Math1Param.cs
src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Math2Param.cs
src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Math3Param.cs
src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Shifts.cs
src/Nethermind/Nethermind.Evm/SpecFlags.std.cs
src/Nethermind/Nethermind.Evm/DispatchFlags.std.cs
src/Nethermind/Nethermind.Evm/VirtualMachine.cs
src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs
src/Nethermind/Nethermind.Evm/VirtualMachine.OpcodeHandlers.cs
src/Nethermind/Nethermind.Evm/VirtualMachine.std.cs
src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs
src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs
src/Nethermind/Nethermind.Specs/Forks/NamedReleaseSpec.cs
src/Nethermind/Nethermind.Specs/Forks/05_SpuriousDragon.cs
src/Nethermind/Nethermind.Specs/Forks/06_Byzantium.cs
src/Nethermind/Nethermind.Specs/Forks/07_Constantinople.cs
src/Nethermind/Nethermind.Specs/Forks/08_ConstantinopleFix.cs
src/Nethermind/Nethermind.Specs/Forks/09_Istanbul.cs
src/Nethermind/Nethermind.Specs/Forks/10_MuirGlacier.cs
src/Nethermind/Nethermind.Specs/Forks/11_Berlin.cs
src/Nethermind/Nethermind.Specs/Forks/12_London.cs
src/Nethermind/Nethermind.Specs/Forks/13_ArrowGlacier.cs
src/Nethermind/Nethermind.Specs/Forks/14_GrayGlacier.cs
src/Nethermind/Nethermind.Specs/Forks/15_Paris.cs
src/Nethermind/Nethermind.Specs/Forks/16_Shanghai.cs
src/Nethermind/Nethermind.Specs/Forks/17_Cancun.cs
src/Nethermind/Nethermind.Specs/Forks/18_Prague.cs
src/Nethermind/Nethermind.Specs/Forks/19_Osaka.cs
src/Nethermind/Nethermind.Specs/Forks/20_BPO1.cs
src/Nethermind/Nethermind.Specs/Forks/21_BPO2.cs
src/Nethermind/Nethermind.Specs/Forks/25_Amsterdam.cs
src/Nethermind/Directory.Build.targets
```

## Generated and proved result

The IR names 26 opcodes and 104 source-bound table specializations. Lean emission
uses a deserialize-after-serialize copy of that IR. That case-sensitive,
required-constructor boundary rejects malformed JSON, unmapped members, missing
fields, nested null values, and any changed
header/root/fork/gas/descriptor/specialization/obligation field, including
duplicate, missing, arbitrary, or unbound specialization roots. The 41 manifest
source paths are unique. The generated file contains
definitions only and does not import the handwritten `PureStackExecution`
machine. It intentionally shares the foundational `Word`, `Gas`, and bounded
`Stack` definitions with the reference, so the separate theorem is a structural
opcode-step refinement over those common primitives, not an independent proof
of their implementations. It proves opcode-byte identity and injectivity,
schedule/gas/stack lemmas, and equality of the restricted single-opcode outcome
with `PureStackExecution`.

## Remaining obligations

These artifacts do not prove C# semantics merely because Roslyn admitted the
source. In particular, the following connections remain open:

- C# compilation and CLR execution preserve the admitted syntax;
- Nethermind `UInt256` and `Int256` operations implement the Lean word
  functions, including signed corner cases;
- big-endian stack-slot layout, unsafe by-reference access, scalar code, and all
  hardware-specific SIMD branches are equivalent to those word functions;
- the JIT produces the named generic specializations and function pointers with
  the modeled behavior;
- tail-call chaining, opcode counters, tracing callbacks, and cancellation have
  no unmodeled effect on the restricted stack/gas/program-counter result.

Memory and storage opcodes, calls and creation, halting, logs, precompiles,
transaction and block accounting, state persistence, concurrency, and RPC are
outside this extractor. This artifact cannot support a claim that the entire
EVM or Nethermind has been formally verified.

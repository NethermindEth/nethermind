# Amsterdam pure-word execution reference

Status: independently reviewed and accepted handwritten reference imported by
`Eip803x.lean`; not a production extraction or refinement.

## Covered execution surface

`PureStackExecution.lean` wraps the existing `PureStack.execute` semantics for
all 26 pure Amsterdam word opcodes.  It adds a top-first, bounded (1024-word)
stack, a program counter, the execution part of `GasState`, explicit
`success`/`outOfGas`/`stackUnderflow` outcomes, and caller-configurable gas
schedule inputs.

| Fixed-cost family | Opcodes | Amsterdam input |
| --- | ---: | ---: |
| very low | 16 | 3 |
| low | 7 | 5 |
| mid | 2 | 8 |
| EXP base | 1 | 10 |
| EXP byte (EIP-160 selected) | dynamic | 50 |

The low family deliberately includes `SIGNEXTEND` and `CLZ`; `CLZ` is not
very-low.  The candidate's executable 26-entry cost table and family theorems
make this classification exhaustive.  The schedule exposes all five values,
so alternate fork pricing can be supplied without changing the semantics.

## Ordering captured

For every selected opcode, `execute` advances `pc` before it attempts any gas
or stack operation.  It then performs these steps in production order:

1. debit the opcode's fixed execution-gas cost;
2. on insufficient gas, return `outOfGas` with `gasLeft = 0`, preserving the
   bounded stack and the EIP-8037 state-gas accounting fields;
3. otherwise, check the operation's arity and return `stackUnderflow` with the
   already-debited gas and unchanged input stack;
4. on success, apply the bounded stack transformation refining
   `PureStack.execute`.

`EXP` is intentionally separate after its fixed charge: it atomically removes
base and exponent; an underflow therefore leaves the original stack intact.
For a non-zero exponent it computes its canonical big-endian byte length and
charges `expByte * byteLength` only after that removal.  Dynamic OOG consequently
has zero execution gas and leaves the post-pop tail observable.  Zero exponent
has byte length zero and produces one after only the base cost.

## Production evidence used

* `src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs:228-249` increments
  `pc`, then has checked handlers debit gas before `EnsureDepth`, and retains
  the increment on either exit.
* `src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs:508-519` makes
  an unaffordable `UpdateGas` set `gas.Value = 0`; a successful charge calls
  `ConsumeRaw`.
* `src/Nethermind/Nethermind.Core/GasCostOf.cs:10-12,36-38` supplies 3/5/8,
  EXP base 10, pre-EIP-160 EXP byte 10, and EIP-160 EXP byte 50.
* `src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Math1Param.cs:51-59`,
  `Math2Param.cs:51-60`, `Math3Param.cs:22-30`, and `Bitwise.cs:77-85` all
  charge before their core depth check.
* `src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Math2Param.cs:409-449`
  shows `EXP` fixed debit, atomic `PopUInt256`, zero-exponent fast path,
  byte-length calculation, dynamic debit, and result push in that order.
* `src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs:349-352`
  selects the EIP-160 byte price; `VirtualMachine.OpcodeHandlers.cs:620-628`
  charges `CLZ` at low cost.  Shift wrappers and their hardware-dependent
  dispatch condition are at `OpcodeHandlers.cs:649-683`.

## Formal inventory

The accepted reference contains 18 execution theorems and 19 vector theorems,
including:

* exhaustive 26-opcode static-cost table and four family theorems;
* bounded unary, binary, and ternary stack transitions;
* a theorem that every successful bounded transition refines
  `PureStack.execute`;
* bounded-output, static-OOG, EXP-underflow, and post-pop dynamic-EXP-OOG
  theorems; and
* a local debit theorem that preserves state-gas accounting while matching the
  production execution-gas OOG clear.

`PureStackExecutionVectors.lean` has 26 successful opcode vectors, then
boundary vectors for static OOG/exact gas, unary/binary/ternary underflow,
zero/dynamic EXP, exponent byte-length thresholds, signed and zero-divisor
arithmetic, shifts at 256, and full-stack depth preservation.  It also kills
four meaningful mutations: making CLZ cost 6, using EXP byte price 10,
preserving gas on OOG, and charging EXP dynamic gas before the atomic pop.

## Remaining production-refinement obligations

This reference does not claim to discharge the following:

* Refinement from `UInt256`/`EvmWord` limb layout, endian conversions, and the
  x86/SIMD branches to `Eip803x.Evm.Word` operations.
* Refinement from the CLR `ref struct` `EvmStack`, in-place pop/peek paths,
  aliasing, tracing paths, and JIT-dependent checked-body selection to the
  persistent Lean stack representation.
* Decoder/dispatch-table, fork-flag, and opcode-enable proof obligations
  (`CLZ`, shifts, and EIP-160 are assumed selected by Amsterdam here).
* Whole-frame exception rollback, memory/state interaction, gas refund and
  reservoir lifecycle, and the outer VM's exception propagation.
* Complete production extraction/refinement; the production regression selector
  supplies differential evidence only.

No behavior-changing production defect was confirmed.  One non-functional
source defect was confirmed: `Math2Param.cs:432` describes the EXP charge as a
number of “32-byte words,” while the immediately surrounding code computes
`32 - (CountLeadingZeros >> 3)`, i.e. exponent bytes, and the selected price is
`ExpByte`/`ExpByteEip160`.  The candidate follows the code and gas-price names,
not that inaccurate comment.

## Validation boundary

Independent review accepted all 26 opcode cases, the 3/5/8/10 plus 50-per-byte
Amsterdam schedule, production-matching PC/gas/depth ordering, EXP atomic-pop
ordering, OOG clearing, post-charge underflow, bounded-stack behavior, and the
refinement to `PureStack`. Direct warning-as-error Lean checks and the targeted
nine-job Lake build pass. The integrated warning-as-error production regression
selector passes 2,430/2,430 cases. This is differential evidence and does not
close the production extraction/refinement obligations above.

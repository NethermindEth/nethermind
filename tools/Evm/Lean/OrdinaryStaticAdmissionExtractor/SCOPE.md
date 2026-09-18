# Scope and theorem boundary

The package has exactly two helper entrypoints. It does not model sender recovery, account/code state,
authorization signature checks, intrinsic-gas calculation, fees, nonce mutation, VM execution, receipts, block
finalization, or the complete admission pipeline.

## Static helper

The source-lowered branch order is:

1. sender absent;
2. validation enabled and nonce is `ulong.MaxValue`;
3. contract creation with EIP-3860 and oversized initcode;
4. SetCode contract creation;
5. SetCode authorization list null or empty;
6. EIP-8037 intrinsic execution/floor value above the cap;
7. transaction gas below normalized standard execution intrinsic;
8. transaction gas below normalized floor intrinsic;
9. transaction gas below fixed-width `max(StandardGas, FloorGas)`;
10. validation-enabled EIP-8037 transaction gas above the normalized header limit;
11. validation-enabled legacy transaction gas above the fixed-width
    `header.GasLimit - (parallel ? 0 : header.GasUsed)` allowance;
12. success.

`TransactionResult.Error` and `EvmExceptionType` are compared fieldwise, together with a unique return-site
constructor. Human-readable `ErrorDescription`/detail text is an explicit observation exclusion because branches
6, 7, and 9 intentionally share an error enum. The return-site identity preserves those distinctions.

The exact skip flag is source-bound to `ExecutionOptions.SkipValidation` (`4`), and the model input is the Boolean
projection returned by `opts.HasFlag(SkipValidation)`, not the raw options word. It only gates nonce and block-limit
checks; it does not gate sender, initcode, SetCode, cap, execution-intrinsic, floor, or minimum checks. Exact
`options == SkipValidation` routes to the excluded system processor; composite flags may retain this Boolean while
remaining on the base helper. The `processorParallel` bit is retained for the legacy allowance. Source-width premises are separate from the exact
all-input theorem: `ulong` values are `Nat <= 2^64-1`, `long` values are signed `Int` values in
`[-2^63, 2^63-1]`, data/authorization lengths are `Nat <= 2^31-1`, and `TxType` is byte-backed.

## Initialization helper

`TryCreate` is composed from the canonical `Eip803x.Generated.TransactionGasInitializationKernel`. The manifest
pins its IR, source manifest, generated Lean, refinement, and mathematical `TransactionGas` target; the canonical
source manifest is also checked against the admitted production C# bytes. It preserves unchecked `ulong` addition of execution gas and the
unchecked-cast signed reservoir, `ulong` subtraction/wrap, the EIP-8037 cap and `Math.Min`, the signed reservoir
cast, and the failure/default branch. The wrapper has separate success/failure return sites and a separate
five-zero available-policy default; the kernel result always retains all six result fields:
`Outcome`, `Value`, `StateReservoir`, `StateGasUsed`, `StateGasSpill`, and `StateGasSpillRefunded`.

## Theorems

`generatedValidateStatic_refines_orderedStaticAdmission` is exact for every source-width static input. It has no
transaction-validity, affordability, cap, or nonnegative-state premise.

`generatedCalculateAvailableGas_refines_fixedWidthInitialization` is exact for every source-width initialization
input. It includes signed and unsigned wrap boundaries and has no validity premise.

`staticAdmissionAccepted_implies_availableGasInitialization` adds only explicit adapter interpretation premises:
EIP-8037 is enabled; the state reservoir is nonnegative and signed-`long` representable; the standard execution
plus state reservoir is within `ulong`; and the post-intrinsic reservoir expression is signed-`long` representable.
From accepted static branches it derives the execution/floor cap inequalities and intrinsic affordability, then
invokes the canonical initializer refinement. It does not assume a pre-built `RefinementValid` bundle. Its
`SourceWidthStatic` premise supplies the remaining source-width facts.

The exact all-input initializer model deliberately retains helper-domain values whose reservoir exceeds `2^63` and
therefore wraps when represented as a signed `long`. This is a theorem-premise boundary, not a claimed reachable
production bug. Ordinary intrinsic construction currently supplies zero state; nonzero state vectors are helper-domain
witnesses for the adapter surface, not a production reachability claim.

The explicit initializer operations are source-bound as `unchecked`. `IntrinsicGas.StandardGas` addition and the
legacy header allowance subtraction rely on the current project/compiler default unchecked context. The package
models that fixed-width behavior but leaves compiler-option evaluation outside its source-to-Lean claim.

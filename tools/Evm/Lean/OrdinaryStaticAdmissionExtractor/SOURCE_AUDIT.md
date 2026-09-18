# Source audit

The extractor reads twenty exact source files listed in the generated manifest. The main production identities are:

| surface | source-bound members |
| --- | --- |
| processor | `TransactionProcessorBase<TGasPolicy>.ValidateStatic`, virtual `ValidateGas`, virtual `CalculateAvailableGas` |
| standard routes | synchronous `BlockProcessingModule` registration and `EthereumTransactionProcessor`; BAL `TransactionProcessorFactory<EthereumGasPolicy>` and `BalTxProcessorFactory` |
| initializer | `TransactionGasInitializationKernel.TryCreate` and all six result fields |
| bridge | `EthereumGasPolicy.TryCreateAvailableFromIntrinsic`, all five policy assignments/default, and direct `Value`/`StateReservoir` accessors |
| intrinsic | `IntrinsicGas<TGasPolicy>.StandardGas`, `MinRequiredGasLimit`, `ExceedsCap` |
| transaction predicates | sender/nonce/To/DataLength, SetCode, authorization-list, `IsAboveInitCode` |
| typed results | exact `TransactionResult.ErrorType`, `EvmExceptionType`, and initializer-outcome enum layouts; `TransactionResult` constructor/error/EVM mappings and `WithDetail`; `ValidationResult` positional `Error`, string conversion, success and Boolean semantics |
| limits/flags | `IReleaseSpec`, `MaxInitCodeSize`, block header widths, EIP-7825 cap, exact `SkipValidation` bit |

Roslyn C# 14 lowering is fail-closed. It records typed operands, reduction operators, ordered branch comparisons, complete
enclosing guard paths, and the synthetic positive-EIP fallthrough that dominates the legacy `ValidateGas` branch.
The emitter checks those typed paths before producing Lean. In particular, it requires the exact Boolean guard
expressions for sender, nonce, initcode, SetCode, cap, validation and fork paths, as well as the exact comparison
operand order and direct return AST form. The EIP-7825 cap is read from the actual typed field declaration, and
`TxType` must remain byte-backed with `SetCode = 4`; a source token hash alone is not semantic lowering.
Standard/floor policy locals, the Ethereum policy accessors, and every emitted comparison operand order are checked
explicitly. The represented transaction, header, spec, policy and initializer fields and accessor signatures are
also checked against their exact `byte`/`int`/`long`/`ulong`/`bool` declaration spellings. The canonical initializer manifest
must name the exact admitted C# source hash and its IR
and Lean artifact hashes; the ordinary manifest pins those artifacts, the canonical refinement, and its
`TransactionGas` target.

The standard runtime paths are only recorded, not proved as CLR/DI behavior: `BlockProcessingModule` registers the
synchronous Ethereum processor and generic Ethereum gas-policy factory, while `BalTxProcessorFactory` constructs
worker processors through that factory. The routing source records
`isSystemTransaction || options == SkipValidation`. Virtual `ValidateStatic`, `ValidateGas`, and
`CalculateAvailableGas` overrides in system, XDC, Taiko, or plugin processors are excluded. Only the helper's
`HasFlag(SkipValidation)` Boolean is modeled; raw/composite option routing and system-route reachability are not.
The extractor uses Roslyn syntax trees rather than a complete project `SemanticModel`; overload and extension-method
symbol resolution outside the exact receiver/member shapes and admitted source closure remains an explicit trust
boundary.

## Deliberate observations

The production result carries dynamic human-readable detail text. This package excludes that text and compares
`Error`, `EvmExceptionType`, and return-site identity fieldwise. The return-site constructors distinguish the three
shared `GasLimitBelowIntrinsicGas` sites. This is an observation boundary, not an assertion that descriptions are
constant.

`StandardGas` is modeled as the current unchecked-context `ulong` addition of execution gas and the unchecked-cast signed
state reservoir. `MinRequiredGasLimit` applies `Math.Max` after that fixed-width result. Legacy `ValidateGas` uses
unchecked-width subtraction for `header.GasLimit - (parallel ? 0 : header.GasUsed)`, including the `GasUsed > GasLimit`
wrap case. The exact Lean theorem keeps these behaviors; natural-number explanations require the stated width/order
premises. The addition and legacy subtraction are implicit-context operations under the current default unchecked
C# compiler setting; compiler-option evaluation remains an explicit external assumption.

The current ordinary intrinsic source construction uses zero state. The IR still exposes the state reservoir because
the bridge receives it and the helper domain must be closed. The nonzero and `> 2^63` reservoir vectors are therefore
helper-domain witnesses. A signed-wrap reservoir is documented as a theorem boundary and is not reported as a
reachable Nethermind defect.

No source/spec discrepancy was found in the audited standard route. The only intentional modeling observations are
the dynamic-description exclusion and the helper-domain state-reservoir boundary above.

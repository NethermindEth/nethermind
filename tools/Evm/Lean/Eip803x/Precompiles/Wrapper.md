# Standard precompile wrapper reference

`Wrapper.lean` is a handwritten executable reference for the control flow shared by
standard-mainnet precompiles. It is grounded in the pinned production implementations of
`IGasPolicy.TryConsumePrecompileGas`, `VirtualMachine.RunPrecompile<Eip158>`,
`ExecutePrecompileCall`, the outer top/full-frame result handling, and the standard-build
inline `STATICCALL` path in `EvmInstructions.Call.std.cs`.

The reference fixes these observable rules:

- base and data prices are separately `UInt64`-representable and are added only after the
  exact `base <= UInt64.MaxValue - data` guard;
- addition overflow rejects without mutating gas; an ordinary insufficient-gas charge zeros
  execution gas through `EthereumGasPolicy.UpdateGas`. The full-frame path discards that local
  zero because `RunPrecompile` installs its local gas only on success, while the inline path
  retains the zeroed child until state-gas restoration and abandonment;
- the full-frame route emits the transfer-log oracle, credits/touches or creates the
  executing account, updates the transaction-sticky RIPEMD-160 restoration flag, and only then
  prices and invokes the leaf. A prior pending flag survives later calls, and post-snapshot
  account existence separately controls whether the historical touch can be replayed;
- a successful nested frame refunds remaining child gas, repays outstanding state-gas spill,
  exposes the full returndata, clips the parent-memory copy, and resumes with stack value one;
- a returned precompile failure carries `EvmExceptionType.PrecompileFailure`, so
  `ExecutePrecompile` routes it through the same exceptional full-child halt branch as
  pricing OOG: the snapshot and historical RIPEMD touch are restored, remaining child
  execution gas is discarded, and only state gas is restored;
- a non-fatal managed exception has no `CallResult` exception flag. In a nested full frame it
  therefore clears child execution gas, uses the revert state-gas transition, restores the
  world snapshot, and resumes with empty returndata and stack value zero;
- a top-level price or returned precompile failure is `OutOfGas`; a top-level non-fatal
  managed exception is surfaced as `PrecompileFailure`. The VM leaves the final transaction
  halt-gas reset to transaction settlement;
- the direct path is disabled by instruction/action tracing and for address `0x03`, because
  only the full route implements the EIP-161/Parity RIPEMD dirty-touch exception;
- direct pricing or leaf failure restores child state gas before abandoning the child, clears
  returndata, pushes zero, burns the forwarded execution gas, and never touches the target
  account. Returned `Result.failure` and non-fatal managed exceptions have the same direct
  observable result;
- direct success touches the target only after the leaf succeeds, refunds remaining child
  gas, retains the entire leaf output as returndata, copies only the requested prefix, and
  pushes one.

The event list is proof-only and chronological. `transferLogOracle` records the production
call site without claiming a model of EIP-7708 log contents. The full-frame account facts
(`wasCreated`, deadness, prior sticky restoration state, and post-restore existence) are adapter
observations rather than a world-state implementation. Hard-failure event order records state-gas
restoration before the child disposal/abandonment marker because production reads the child gas
before disposing that frame.

## Claim boundary

This is a handwritten wrapper reference, not a production refinement theorem. It deliberately
treats each precompile's cost calculation and `Run` computation as an oracle (`Cost` and
`LeafResult`); the wrapper's overflow check and debit behavior are modeled here.
It does not prove precompile dispatch/registration, cryptography, native libraries, allocation,
world-state journaling, balance arithmetic, EIP-7708 log construction, tracer output, logger
behavior, CLR exception filters, or `Environment.Exit` for a missing native dependency. The
`DllNotFoundException` environment-exit branch is excluded rather than modeled as a returning
outcome. Machine-width preservation for gas refund/state restoration remains an adapter
obligation; those mathematical transitions are already separate extraction/refinement targets.
The full nested composition assumes the precompile child has no advanced ancestor-state refund;
that counter is zero on frame entry and a precompile leaf cannot mutate VM gas state.
The direct production path does not construct a `CallResult`, exception flag, or substate error;
accordingly its outcome exposes only the opcode exception, returndata, copied bytes, and stack bit.

The inline input/output memory ranges have already been expanded and charged by the CALL handler.
The reference retains the defensive input-memory rejection, but assumes the clipped output copy
meets that earlier range proof. It starts after CALL base/access/new-account charges and after
`TryReserveChildGas`; therefore it does not claim the surrounding CALL handler or EIP-150 reserve
calculation.

The full nested outcome also starts after the surrounding CALL handler's optional
`NEW_ACCOUNT` state charge. Its failure-side `CreditStateGasRefund` is deliberately left to the
separate CALL/state-gas composition theorem; `executeNested` is directly applicable when that
up-front charge is absent (including `STATICCALL` and calls to an existing recipient).

The slice contains 31 named theorems. `WrapperVectors.lean` provides 38 independent executable
examples covering exact and one-short charging, overflow, sticky RIPEMD restoration,
top/full-frame and inline success/failure/managed-exception, literal nonzero state-gas formulas,
returndata/copy, exact hard-failure event order, and six mutation sentinels.

Reproduce the bounded proof check with:

```powershell
lake build Eip803x.Precompiles.Wrapper Eip803x.Precompiles.WrapperVectors
```

# Persistent-storage handler reference

`PersistentStorage.lean` is a handwritten, executable handler-local reference for the pinned
standard-mainnet Amsterdam paths:

- `EvmInstructions.InstructionSLoad`;
- the joint EIP-8037/EIP-8038 branch of `EvmInstructions.InstructionSStoreMetered`;
- the `EthereumGasPolicy` execution/state charge and state-refill behavior those handlers call;
- `SStore.price`, which remains the shared pure EIP-8038 original/current/new pricing table.

The protocol sources are the pinned [EIP-8037 revision](https://github.com/ethereum/EIPs/blob/052029f3625328d6f51dec8e62a7090201e66f17/EIPS/eip-8037.md),
[EIP-8038 revision](https://github.com/ethereum/EIPs/blob/8331fb3eed0a5366b28b25a016f1ad04fac0fa8e/EIPS/eip-8038.md),
and [Amsterdam execution-spec revision](https://github.com/ethereum/execution-specs/blob/0cc100eb190b64b23baba72dac0165652eaec252/src/ethereum/forks/amsterdam/vm/instructions/storage.py).
The production source identity is not established by this leaf; it is inherited only when a
separate extraction manifest pins every production source named above.

## State and ordering

The model makes the following data explicit:

- the executing account and 256-bit storage key;
- transaction-start `original` and journaled `current` values;
- a transaction-wide warm-cell set;
- a bounded 1,024-word EVM stack;
- execution gas, state reservoir, outstanding gas-left-funded state gas, state used, and the
  signed transaction refund counter;
- the current frame's state-gas floor and `stateGasRefundAdvanced` amount;
- a proof-only chronological event list for mutation-sensitive sequencing checks.

For the pinned specialized production handler, SLOAD performs its zero Amsterdam base charge,
pops the key, charges and then warms the access, reads current storage, and pushes the value.
SSTORE rejects static context, applies the strict `gas_left > 2300` sentry, pops the key and value
sequentially, charges and warms the access, reads current storage, and reads original storage only
for a change. It then charges execution write gas before state gas, applies the clear-refund and
clear-refund-reversal adjustments, performs any state refill, applies the restore-original refund,
and writes only after every gas effect succeeds.

The sentry and non-gas range/policy checks preserve handler-local remaining gas. A failed
`EthereumGasPolicy.UpdateGas` is represented separately and burns execution gas to zero. A failed
state charge preserves the already-debited execution gas, matching
`TryConsumeStateAndExecutionGas`. Static rejection and every failure leave persistent storage
unchanged. Second-pop underflow preserves the actual production partial mutation: the key remains
consumed.

The pinned Amsterdam values come through `GasSchedule.amsterdam`: cold/warm storage access is
2,100/100, `STORAGE_WRITE` is 10,000, the clear refund is 11,616, and a 64-byte storage creation at
1,530 gas per byte costs 97,920 state gas. The surrounding `Schedule` keeps the EIP-2929 SLOAD base
(zero when active) and EIP-2200 stipend sentry explicit instead of hiding them in transition code.

## Frame-floor refinement boundary

`creditStateGasRefund` deliberately does not equate every credit with `GasMachine.refillState`.
It first refills only `stateUsed - stateGasFloor`, which is state created in the current frame. An
amount above that floor is still returned in source-based LIFO order so the child can spend it, but
it does not decrement the child's `stateUsed`; it increments `stateGasRefundAdvanced`. A successful
frame merge must later discard that amount against the ancestor's floor. Vectors cover both a
locally-created slot and a child clearing a slot created by its parent.

The simplified `GasState.stateFromGasLeft` field represents production's current unrefunded spill.
Production retains total spill and refunded-spill as two fixed-width fields. The existing state-gas
transition extraction handles that representation; this handwritten opcode leaf neither re-proves
that mapping nor models unchecked `ulong`/`long` overflow.

## Proofs and vectors

The leaf contains 37 general theorems. In addition to current-write readback and access-price
lemmas, they universally prove that every non-successful SSTORE preserves storage, the refund
counter, and advanced-refund accounting. Separate arbitrary-input refinements cover no-op and
changed-slot success: the latter identifies the exact `SStore.price` execution charge, state
charge, signed refund, and state refill; proves selected-cell warming, original preservation,
requested-current writeback, and preservation of every other cell; and retains the arbitrary
post-pop stack tail. Universal post-access OOG theorems distinguish execution-charge failure,
which changes only `gasLeft` in the complete post-access gas state and sets it to zero, from
state-charge failure, which preserves the complete post-execution gas state; both retain warmth
and prohibit a write. The local/ancestor refill theorem gives an
equation for every affected gas field, the locally applied amount, and the advanced amount.

These are theorems about the handwritten transition, not refinement theorems for extracted C#.
They also establish static/sentry preservation, exact signed refund decomposition, and
impossibility of SLOAD stack overflow after a successful pop.

`PersistentStorageVectors.lean` contains 36 executable vectors checking:

- cold and warm SLOAD exact/one-short boundaries, including the 99-gas warm failure with a
  consumed key but no read or push, and base-before-stack ordering;
- SSTORE static, stipend, empty-stack, and second-pop underflow cases;
- access OOG before warming/reading and access success at the exact charge;
- zero/nonzero original/current/new cases, no-op, first creation/update/clear, dirty clear/restore,
  cleared restore/rewrite, and created-slot rewrite/reset;
- execution-write and state-charge exact/one-short behavior;
- access warmth surviving later OOG while no failed operation writes storage;
- signed clear/reversal/restore refunds and local versus ancestor state refills;
- explicit mutation sentinels for late stipend checking, premature warming, state-before-execution
  charging, key/value reversal, and write-before-final-charge ordering.

Build this leaf with:

```powershell
lake build Eip803x.Evm.PersistentStorage Eip803x.Evm.PersistentStorageVectors
```

## Exclusions

This is not a formal verification of the production handlers. It is handwritten and has no Roslyn
extraction or equality theorem connecting the complete C# handler AST to Lean. It also excludes
opcode dispatch and program-counter updates, byte-array normalization, world-state/provider and
`GetOriginal` correctness, Block Access List tracing, ordinary tracing and metrics, journal
snapshots, frame rollback/exception cleanup, incorporation/removal of advanced refunds at frame
exit, transaction refund caps, fixed-width overflow, allocation failure, and database persistence.

The pinned execution-spec implementation currently pops SSTORE operands before its combined
affordability check, while the pinned Nethermind handler performs its stipend sentry before stack
access and its access debit afterward. This model intentionally follows Nethermind at that
handler-local boundary. Both paths exceptionally halt without a durable storage write, but this
leaf proves neither intermediate-state nor whole-frame equivalence with that external
implementation.

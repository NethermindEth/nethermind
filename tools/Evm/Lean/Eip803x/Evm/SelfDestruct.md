# Amsterdam SELFDESTRUCT handler reference

`SelfDestruct.lean` is an independently reviewed handwritten handler-local reference for
the standard-mainnet Amsterdam `SELFDESTRUCT` opcode. It composes the legacy
5,000-gas base charge, EIP-8038 beneficiary access and `ACCOUNT_WRITE` charges,
EIP-8037 new-account state gas, EIP-6780 same-transaction destroy marking,
EIP-8246 self-target balance preservation, and EIP-7708 transfer-log behavior
in the current production order.

The Amsterdam proposal pins already recorded in `SPEC.md` supply EIP-8037 and
EIP-8038. The other directly modeled rules are pinned here to
[`EIP-6780@688e939c`](https://github.com/ethereum/EIPs/blob/688e939c4e0968f0cbc6a4e79426b498869eec19/EIPS/eip-6780.md),
[`EIP-8246@9207c601`](https://github.com/ethereum/EIPs/blob/9207c6011f526bd40abd79649484a1a342585bd4/EIPS/eip-8246.md),
and
[`EIP-7708@f7230c46`](https://github.com/ethereum/EIPs/blob/f7230c46a743313957d8f38a159bda934cc735b2/EIPS/eip-7708.md).
The production comparison surface is
[`EvmInstructions.ControlFlow.cs`](../../../../../src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.ControlFlow.cs),
with pricing delegated through the separately modeled account schedule.

`SelfDestructVectors.lean` carries 14 literal cases covering inconsistent
facts, static violation, base/access/account-write/state OOG boundaries,
warm/cold access, zero and positive transfers, absent, empty-leaf and existent
beneficiaries, state-reservoir spill, same-transaction self-targeting, and
pre-existing self-targeting. Twelve mutations constrain the gas constants,
warm charge, account-write and state charge, new-account predicate, destroy
marking, access-OOG warming, state-OOG retention, spill accounting,
self-target preservation, and zero-value account creation. The targeted
eight-job build passes eight model theorems, four vector theorems, and 19
examples. Independent review also kernel-checked every vector theorem and
example without `native_decide`, and 110 focused production cases pass across
the EIP-6780, EIP-8246, EIP-7708, EIP-8038, and EIP-8037 surfaces.

This leaf records immediate handler effects, including effects that a later
exceptional frame rollback must undo. It does not prove the production stack
or address decoder, access-tracker journal semantics, actual world-state
operations, tracing, transaction-level rollback, destroy-list finalization,
EIP-161 reaping, fee-time balance changes, receipt Bloom, production
extraction, dispatch, or frame/transaction/block composition. In particular,
it proves no state refill only for its literal successful vectors; the full
no-refill and finalization claims remain open. Independent review accepts only
this immediate-handler handwritten leaf; none of the excluded adapter,
rollback, finalization, or composition obligations are discharged by that
acceptance.

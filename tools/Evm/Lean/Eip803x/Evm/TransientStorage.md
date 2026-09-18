# Transient-storage reference boundary

`TransientStorage.lean` is the handwritten transaction/frame lifecycle model
for EIP-1153 `TLOAD` and `TSTORE` in the pinned standard-mainnet EVM.

It covers:

- `(executing account, key)` cell identity and zero for an uninitialized cell;
- write/read consistency and separation across accounts and keys;
- visibility to reentrant frames and successful children;
- rollback of reverted or exceptional children, including a successful nested
  child later rolled back with its parent;
- account-scoped clearing, while preserving other accounts' transient cells;
- static-context rejection before mutation; and
- unconditional reset at the transaction boundary.

`TransientStorageStack.lean` separately composes this lifecycle model with a
bounded stack and the configurable `GasSchedule.warmAccess` charge. It models
production's static-check, charge, pop, partial-underflow, write, and failed
charge gas-exhaustion order.

This is not yet a production-refinement claim. Tracing, `CALL` versus
`DELEGATECALL` selection of the executing account, whole-frame exceptional-halt
composition, the Nethermind change-journal implementation, snapshot-index
validity, and the transaction processor's reset/commit reachability remain
separate obligations.

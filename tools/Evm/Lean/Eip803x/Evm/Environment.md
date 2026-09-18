# Context-opcode reference boundary

`Environment.lean` models the value-selection semantics of the 20 context
opcodes separated from account reads, copying, and `GAS` in the pinned
Amsterdam dispatch table. `BLOCKHASH` enforces the 256-block window before
consulting an abstract provider result. `BLOBHASH` is zero outside the
transaction list. `ValidContext` makes the address, collection-length, and
header integer bounds required by the production representations explicit.
Missing production header data for `BLOBBASEFEE` and `SLOTNUM` is represented
as `badInstruction`; valid pinned-mainnet block input must prove those fields
exist. `BlobBaseFeeRepresents` separately relates Nethermind's cached blob fee
to the pinned calculation over `ExcessBlobGas`.

`EnvironmentStack.lean` composes these selected values with the shared bounded,
top-first EVM stack. It covers the pop/replace behavior of `BLOCKHASH` and
`BLOBHASH`, ordinary pushes, underflow, overflow, and missing-header-field
faults.

These are handwritten executable references, not production refinements. They
do not model opcode gas, PC movement, dispatch reachability, the production
address/word byte encoding, or the correctness of the block-hash provider,
world-state balance lookup, blob-base-fee calculator, and context adapters.
Those obligations require a source-extracted production path before any
opcode-level production claim is made.

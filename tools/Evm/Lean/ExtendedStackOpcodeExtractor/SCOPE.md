# EIP-8024 operational extractor scope

This package pins the standard-mainnet Amsterdam production route for exactly
`DUPN` (`0xe6`), `SWAPN` (`0xe7`), and `EXCHANGE` (`0xe8`). It admits the
complete production files by raw SHA-256 and Roslyn token SHA-256, then selects
the exact source path, namespace, generic owner, member kind, method generic
arity, parameter count, and parameter types used by the route. It also pins
the standard-versus-zkEVM build selector, Ethereum VM DI root, Ethereum gas
policy, four tracing/cancellation dispatch tables, default bad-instruction
handler and its exact `InstructionBadInstruction` implementation, the
`VeryLowGasCost` forwarding tag, every fork ancestor, and
Amsterdam's `IsEip8024Enabled` assignment.

The extracted operational order is narrow and explicit. `ExecuteOpcode`
advances from the opcode to its immediate first. The ordinary EIP-8024 body
then debits `VeryLow` gas (three), exhausting gas on failure. An invalid
immediate returns `BadInstruction` without consuming that immediate. A valid
immediate advances PC once more before the stack operation; underflow and the
`DUPN` overflow therefore retain the paid gas and consumed immediate. A missing
immediate reads as zero and is valid. Before Amsterdam, the table retains its
terminating `BadInstructionOpcode`, advances only the opcode PC, and performs
neither the EIP-8024 gas debit nor stack mutation. The admitted control-flow
member is exactly an expression-bodied `BadInstruction` return, so it cannot
write through the stack or gas references.

The canonical JSON is fail-closed: names are case-sensitive; constructor fields
are required; unmapped properties, null collections, null entries, null or
empty claim fields, duplicate identities, non-lowercase hashes, and any value
that differs from the admitted profile are rejected. IR, manifest, and Lean
bytes are deterministic. The generated Lean is theorem-free and implements its
own read, replacement, duplicate, swap, exchange, gas, PC, fork, and fault
transitions. It imports the previously source-derived decoder kernel, but never
imports or calls `Eip803x.Evm.ExtendedStack`.

The separate refinement imports the generated program and the existing
handwritten `Eip803x.Evm.ExtendedStack` model. It proves decoder projection,
stack mutation, fixed gas, fork rejection, PC positions, invalid-immediate,
underflow, overflow, missing-immediate, and full single-opcode outcome
correspondence at this boundary.

This is not whole-loop, frame, or CLR verification. Unsafe slot layout and
`UInt256` representation, JIT generic specialization, function pointers and
tail calls, tracing, cancellation, opcode counters, frame settlement, memory
and allocation behavior, transaction/block integration, alternate gas
policies, and zkEVM execution remain provider obligations. No theorem in this
package should be read as discharging them.

# Stack-rearrangement opcode extractor scope

This package pins the standard-mainnet Amsterdam production route for exactly
`POP` (`0x50`), `DUP1`--`DUP16` (`0x80`--`0x8f`), and `SWAP1`--`SWAP16`
(`0x90`--`0x9f`). It admits the complete Roslyn-parsed source closure through
an embedded path/raw-SHA-256 review index, checks the standard/zkEVM selector,
and records the Ethereum VM, DI, gas-policy, dispatch, and full fork lineage.

The serialized IR is strict: constructor fields are required, unknown JSON
members and case aliases are rejected, null collections/entries/fields are
rejected, and all descriptor, gas, stack, ordering, fork, obligation, and
fully-closed four-table root fields are compared with the exact 33-opcode
profile after deserialization. Regeneration is deterministic. Every opcode has
four roots (`NoTrace`, `NoTraceCancelable`, `Traced`, and `TracedCancelable`),
for 132 roots total.

The generated Lean file is theorem-free metadata plus an executable plan that
consumes its descriptor fields. It imports only the foundational `GasState`,
`UInt256`, and bounded stack representation; it owns its own list/head/get/set/
pop/duplicate/swap transitions and uses `Stack.fromWords` only to repackage
their bounded list result. It never invokes the handwritten target transition.
The proof-bearing refinement imports the separate handwritten reference and
proves that each generated list transition and repackaging equals
`MemoryStackControl`'s bounded `pop`/`duplicate`/`swap` operations. Its gas
debit, PC-before-gas convention, fault ordering, and plan are independently
defined. The refinement proves the exact root coverage, two/three gas costs,
PC advancement before gas, out-of-gas exhaustion, post-charge
underflow/overflow, and exact stack results for all 33 operations. Deliberately
wrong PC, gas, gas-exhaustion, and stack routes have negative checks.

This is not a proof of Nethermind's CLR execution. The following remain open
provider obligations: unsafe `EvmStack` slot layout and `UInt256` conversion;
JIT specialization and function-pointer/tail-call behavior; tracing and
cancellation; opcode counters; code/frame settlement; memory and allocation
limits; transaction/block integration; and any interactions outside the one
bounded opcode transition. Those gaps are intentionally kept in the IR
obligation list rather than silently modeled as proven.

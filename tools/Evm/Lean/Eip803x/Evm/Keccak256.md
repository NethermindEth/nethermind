# KECCAK256 handwritten reference boundary

`Keccak256.lean` models the local transition implemented by
`EvmInstructions.InstructionKeccak256`: atomic offset/length pop, checked ceiling word count,
the size-derived charge before its overflow flag, bounded memory expansion and its charge,
the exact zero-extended memory slice supplied to hashing, and replacement of the two operands
with the 256-bit result.

The hash primitive is an explicit `HashOracle` argument. The model proves which bytes cross that
boundary; it does not prove Keccak's permutation, `KeccakCache`, `ValueHash256` byte layout, opcode
dispatch, tracing, program-counter movement, allocation behavior, frame-level exceptional-halt gas
settlement, or refinement of the production handler. Those remain separate manifest gates.

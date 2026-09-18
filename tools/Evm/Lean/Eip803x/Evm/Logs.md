# LOG0–LOG4 handwritten reference boundary

`Logs.lean` models the shared standard-mainnet `InstructionLog` handler for `LOG0` through
`LOG4`. It covers static-context rejection, the atomic offset/length stack pop, the bounded
memory range and Yellow-Paper expansion cost, both execution-gas charges, topic order and
partial topic underflow, memory payload extraction, and append-only log creation.

The transition is a handler-local, pre-dispatch relation and follows Nethermind's local order.
Calculating a valid
memory expansion installs the new logical memory size before charging it. Insufficient expansion
gas therefore burns execution gas and retains that logical size. An invalid non-empty memory
range reports out-of-gas after the header pop without changing gas or memory; the surrounding
dispatcher later clears execution gas for the exceptional frame outcome. Emission gas is charged,
then the payload is read, before any topic is popped, and no log is appended unless every topic is
available.

This is a proved handwritten reference, not a production refinement theorem. The C# handler,
`EvmPooledMemory`, `EthereumGasPolicy`, `EvmStack`, `VirtualMachine.AddLog`, opcode dispatch,
frame rollback, tracing callbacks, allocation failure, fixed-width arithmetic, and receipt Bloom
construction are not yet extracted. The standard opcode family restricts topic counts to zero
through four; the model exposes only those five constructors. The schedule is configurable, and
the Amsterdam instance records the current LOG and memory constants. A future production relation
must also prove the reachable representation of `Address` by the low 160 bits of `UInt256`, bounded
and word-aligned memory, folded memory-position inputs, and that a successful expansion makes the
subsequent production `TryLoad` total in the modeled domain.

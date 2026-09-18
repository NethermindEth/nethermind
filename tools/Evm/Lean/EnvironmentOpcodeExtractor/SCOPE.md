# Environment opcode extraction scope

This extractor admits the 20 context/environment opcodes modeled by
`Eip803x.Evm.Environment` at the pinned Amsterdam source snapshot. It binds each
opcode byte to its unique executable `GenerateOpcodeHandlers` assignment, exact
activation gate, handler type template, checked or unchecked wrapper, fixed-gas
tag, value provider or special instruction body, and the standard
`EthereumVirtualMachine` / `EthereumGasPolicy` block-processing registration.
The semantic IR contains the exact 20-by-4 Cartesian product of standard
dispatch-table specializations. Each root closes nested gas-policy parameters to
`EthereumGasPolicy`, tracing parameters to the table's `OffFlag` or `OnFlag`,
cancellation to its table flag, and the continuation parameter to `OnFlag`.

Every selected declaration is admitted by a complete normalized-token SHA-256
fingerprint under an owner-, resource-, and source-path-qualified identity;
duplicate identities are rejected before the manifest is emitted. Roslyn rejects parse errors, directives, disabled text, skipped
tokens, duplicate or out-of-range selected bytes, duplicate/competing dispatch
assignments, nested closure/local-function anchors, changed generic targets or
arguments, changed gates, inherited-fork deactivation, changed special-handler
ordering, and any unmodeled token change in an admitted declaration. Checked
artifacts must byte-match a fresh extraction. Generated Lean is produced only
after JSON serialization and deserialization of the semantic IR and contains
definitions but no theorems or imports of the handwritten environment model.
Deserialized-IR validation uses case-sensitive required-constructor binding and
rejects malformed JSON, unmapped members, missing fields, null
collections/entries/fields, and any change to the header, reachability record,
opcode descriptors, specialization order or roots, or declared obligations. It
also rejects arbitrary, duplicate, missing, unknown, or unbound specialization
roots. The named-fork singleton constructor and both
mutually exclusive standard/zkEVM source-selection groups are checked
semantically; the complete build target is additionally admitted by raw hash.

The refinement proves the extracted byte, handler-route, fixed gas, dispatch and
semantic stack tables; the exact unique 20-by-4 specialization key set, table
flags, and fully closed roots; and value selection for all 20 opcodes. The
independent `EnvironmentStack.executeAmsterdam` specification defines the
gas/PC/stack/status transition without importing or referring to generated
semantics. The single `closed_amsterdam_refines` theorem relates the theorem-free
generated `executeAmsterdam` transition to that independent specification for
every opcode and every dispatch table. It covers success, OOG with gas clearing,
stack underflow after charge for `BLOCKHASH`/`BLOBHASH`, stack overflow after
charge for pushing opcodes, the one-byte PC advance, both missing-context
bad-instruction outcomes before gas charging, and all block-hash/blob-hash
provider results encoded in the admitted context. Its only input premise is the
concrete `ContextWellFormed` representation predicate; it does not accept a
caller-provided semantic relation. Separate adapter theorems relate `BLOCKHASH`
to the admitted production provider, including its out-of-window result, and
cached blob base fee to the production calculator.

This is an operational state/status projection. The four dispatch-table roots
remain distinct and source-bound, while trace callbacks and cancellation are
intentionally not fields of the projected state. The theorem therefore does not
claim exact callback-event or cancellation-scheduling refinement.

Open obligations are deliberately explicit:

- C# binding and semantics, CLR/JIT/AOT, unsafe stack and function-pointer
  dispatch, InlineIL tail calls, OS and hardware correctness;
- representation equivalence for `Address`, `ValueHash256`, `UInt256`, byte
  order, EVM stack slots, and every concrete push/pop helper;
- correctness and completeness of `IBlockhashProvider`, world-state balance
  lookup, transaction/block context construction, blob-versioned-hash input,
  slot input, and the cached blob-base-fee calculator;
- tracing/cancellation callback side effects and exceptions;
- composition with unrelated opcodes, frames, transaction settlement, block
  processing, precompiles, state roots, and persistence.

Consequently this slice is not a claim that the EVM or Nethermind is fully
formally verified.

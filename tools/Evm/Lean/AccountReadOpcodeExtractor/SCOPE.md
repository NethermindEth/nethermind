# Account-read opcode refinement scope

## Closed claim

For the admitted standard build and root-first `Amsterdam` named-fork configuration, this slice closes the four production dispatch routes for `BALANCE` (`0x31`), `EXTCODESIZE` (`0x3b`), `EXTCODECOPY` (`0x3c`), and `EXTCODEHASH` (`0x3f`) through all four standard tracing/cancellation tables over `VirtualMachine<EthereumGasPolicy>`.

The admitted source and round-tripped semantic IR bind:

- exact opcode bytes, one production assignment per byte, activation, wrapper and closed generic roots;
- standard/zk build selection, standard opcode-table construction/refresh, `IVirtualMachine`, `IWorldState`, code-repository, precompile-provider and code-cache DI, and complete Olympic-to-Amsterdam ancestry;
- Amsterdam zero base read costs, cold `3000`, warm `100`, copy-word `3`, very-low `3`, and memory `3*w + w*w/512` metadata;
- warm-up-before-charge, precompile-as-warm classification, account-access pricing, gas exhaustion, and the EIP-8038 second warm charge for `EXTCODESIZE` and `EXTCODECOPY`;
- low-160-bit address pop, stack effects, `BALANCE` and dead-account `EXTCODEHASH` value selection;
- `EXTCODESIZE` no-delegation code length and its untraced `ISZERO`/`GT`/`EQ` fusion, including local PC/opcode-count and very-low gas effects;
- `EXTCODECOPY` address-first/atomic-triple pop behavior, length bound, copy-word gas, logical memory expansion during cost calculation before the gas debit, zero extension, no-delegation code selection, explicit account reads, and the zero-length bytecode-access record.

The generated Lean module contains no theorem and imports neither the handwritten account-read reference nor `AccountPricing`. Generated and handwritten machines share the theorem-free `AccountReadTypes` representation boundary and the proved `MemoryGas.prepare` foundation used for EXTCODECOPY memory expansion; each defines its own account-read schedule and opcode transition functions. The independent refinement proves exact metadata, Cartesian completeness and uniqueness of all 16 roots, absence of open root placeholders, EIP-8038 pricing agreement, and equality between those two abstract transitions. It does not connect an observed production frame or provider directly to either abstract state.

## Fail-closed boundary

Every selected production declaration is admitted by a complete Roslyn token fingerprint. The extractor rejects parse/skipped/disabled selected source, duplicate or out-of-range bytes, alternate/duplicate/later/dead-code dispatch assignments, wrong generic roots, changed fork/build/table/DI paths, reordered or replaced gas/provider/stack/memory effects, unmapped or malformed JSON, null IR values, and any arbitrary, duplicate, missing, unknown, or unbound round-tripped IR root. The raw `Directory.Build.targets` hash and both exact mutually exclusive item groups are checked.

## Open obligations

- The Roslyn admission and handwritten semantic transcription do not verify C#, Roslyn, CLR, JIT, or unsafe-memory correctness.
- `Address` is assumed to equal the low 160 bits of the popped `UInt256`; `UInt256`, `ValueHash256`, and byte-order representations remain adapter obligations.
- `IWorldState`, `ICodeInfoRepository`, cache, precompile, dead-account, and contract/code-length results are assumed coherent with the Lean `AccountView` provider.
- The consensus theorem assumes `VirtualMachine.IsTracingAccess = false`. Access-list-generation tracing deliberately performs an extra pre-warm; instruction tracing is modeled only where it changes EXTCODESIZE fusion.
- Tracer callbacks, cancellation polling, cache metrics, database persistence, journal/frame rollback, and transaction/block settlement are outside this slice.
- The modeled PC is the dispatch-local PC. Publishing or discarding that PC on exceptional frame exit remains a dispatcher/frame obligation.

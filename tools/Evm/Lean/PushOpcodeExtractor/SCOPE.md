# PUSH opcode production-refinement scope

This package admits the 33 Amsterdam standard-mainnet PUSH opcodes (`PUSH0`, `PUSH1` through `PUSH32`) and their four tracing/cancellation dispatch specializations. It is a bounded production-refinement slice, not verification of the whole EVM, transaction processor, block processor, CLR, JIT, or unsafe runtime.

The extractor pins 42 complete production sources and validates the exact opcode bytes, fixed gas tags and Ethereum gas-policy charge order, stack limit, wrappers, standard table construction and refresh route, four closed `VirtualMachine<EthereumGasPolicy>.ExecuteOpcode` roots per opcode, mainnet DI registration, mutually exclusive standard/zk source selection, `NamedReleaseSpec<TSelf>.Instance = new TSelf()`, the complete Amsterdam ancestry, and Shanghai's `IsEip3855Enabled` activation inherited by Amsterdam. Every admitted source has a path-qualified complete-compilation-unit identity. Duplicate sources, admissions, opcode keys, bytes, root keys, and closed roots fail closed.

The generated Lean is emitted only after the JSON IR is serialized, deserialized with case-sensitive required-constructor and unmapped-member rejection, and validated against every exact field. It contains definitions only. The independent specification shares only the theorem-free machine data types and the already proved `Eip803x.Evm.MemoryStackControl` byte/word/stack foundation. The refinement covers:

- opcode-byte decoding, fixed base/very-low charge, opcode-count and PC entry;
- gas failure before stack checks, stack overflow behavior, and the checked-body terminal elision;
- traced and non-terminal immediate pushes using zero-extended `readRange`, including truncated values and terminal PC overshoot;
- the special untraced `PUSH2` overflow/terminal path and its `PUSH2;JUMP` / `PUSH2;JUMPI` fusion projection;
- fused jump/JUMPI/JUMPDEST gas and opcode counts, zero-condition fallthrough, condition underflow, invalid destination, and post-destination out-of-gas ordering;
- all 33 opcode metadata records and all 132 unique closed dispatch roots.

The fusion theorems compare production's optimized transition with independent ordinary `PUSH2; JUMP` and `PUSH2; JUMPI` definitions that materialize the destination on the stack and then pop it in the following opcode. Under explicit sufficient-gas, stack-room, nonterminal-code, valid-destination, and (for `JUMPI`) condition-pop premises, they prove exact status, gas, opcode-count, stack, and PC equality. The ordinary definitions do not call the fused helpers, so the proof establishes the real temporary destination push/pop cancellation. Exceptional and invalid-destination fusion equivalence remains outside this theorem; push-charge out-of-gas ordering is proved separately. The jump-destination analyzer itself remains an explicit premise.

Open premises and obligations are explicit in the IR: unsafe code-buffer/sentinel validity; EvmStack byte-lane representation and optimized writer correctness; EthereumGasPolicy/CLR fixed-width execution; jump-destination provider correctness and prefetch purity; tracer/cancellation observational transparency; and CLR/JIT/function-pointer realization of the admitted roots. The untraced terminal optimization intentionally omits a stack write only when no later opcode or tracer can observe it.

The mutation suite covers arbitrary complete-source drift, duplicate/wrong dispatch, opcode bytes, gates, wrapper fields, gas/order, terminal and padding rules, fork replay/activation/singleton, build selection, VM/DI routes, strict malformed/unknown/null/case/omitted JSON, lowercase fixed-width digest admission, every serialized scalar field, duplicates, out-of-range bytes, missing roots, unbound placeholders, deterministic artifacts, generated/reference separation, and independent byte identity.

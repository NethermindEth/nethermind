# CALL/CREATE opcode extraction scope

This package is a fail-closed, standard-mainnet Amsterdam operational
extraction for `CALL`, `CALLCODE`, `DELEGATECALL`, `STATICCALL`, `CREATE`,
`CREATE2`, and `SELFDESTRUCT`. It closes four traced/cancelable dispatch-table
specializations per opcode: 7 opcodes and 28 exact generic roots. The production
closure contains 79 C# sources, 4 raw build/representation/reference inputs, and 105 exact
Roslyn syntax admissions. `SOURCE_AUDIT.md` records the admitted call graph.

The generated Lean kernel is theorem-free. It is emitted from strictly
deserialized canonical semantic IR and a pinned operational template. It does
not import, invoke, or name the executable transitions in the separately pinned
`CallCreateOperational`, `Eip803x.Evm.CallCreateFrame`, or
`Eip803x.Evm.SelfDestruct` references. Only the refinement side imports those
independent specifications.

## Bounded claim

The generated transition covers opcode-table reachability, instruction
start/PC/count order, exact sequential stack residue, Amsterdam execution and
state-gas charging, memory expansion, access warming, EIP-150 reservation,
stipend handling, child-frame construction, CREATE collision, the bounded VM
resume/merge and code-deposit paths, stack result and clipped output copy, and
the instruction/action tracing boundaries owned by production. Inconsistent
CREATE physical/logical/collision facts are rejected before warming or gas
mutation. A resumed child is rejected before merge unless its frame baselines,
remaining-gas bound, spill bound, and EIP-8037 frame invariant agree with the
staged entry.

Every CALL-family code-source stack word is reduced to its low 160 bits at the
pop boundary before access warming, delegation, targeting, child staging, or
inline-precompile classification. Thus `2^160 + 3` is RIPEMD160 address 3, not
a distinct inline-eligible value. The standard inline STATICCALL/precompile
branch is eligible only without instruction or action tracing and when the
normalized code source is not RIPEMD160. RIPEMD160 therefore stages a child
frame. Inline oracle remaining gas must
not exceed the reserved child gas. Successful CREATE first refunds child gas
into the parent, charges code-deposit execution and state gas on that parent,
commits the child journal, and only then repays state-gas spill. Deposit failure
uses the separate refunded-child-to-halt residue and restores the CREATE
snapshot.

SELFDESTRUCT independently applies the same production `PopAddress` rule to
the beneficiary immediately after the stack pop and before warmth, access gas,
action tracing, dead-account facts, or the supplied world transition. Thus the
asymmetric stack word `2^160 + 3` is observed as beneficiary address `3` by all
modeled downstream effects; raw stack residue before the pop is not rewritten.

The shared module contains only inert data and result vocabularies. Generated
and reference semantics independently define dispatch-table tracing, low-160
address interpretation, and CREATE deposit phase order, so mutating either
side changes or breaks the refinement rather than changing a shared premise.

The package-wide `closed_amsterdam_refines` theorem quantifies every dispatch
table, every one of the seven opcodes, every handler oracle, every machine
state, and every supplied child outcome. Its first equality covers the complete
immediate transition and its second equality covers resume and settlement. No
caller-provided equality or semantic relation is an input to that theorem.

The refinement also proves the 28 roots and semantic opcode mapping are complete;
Amsterdam constants and CALL/CREATE/SELFDESTRUCT pricing predicates agree with
the independent handwritten models; success, REVERT, exception, upfront-state
refill, and CREATE deposit-failure gas projections use the handwritten frame
merges; failed CREATE restores the captured world/journal projection; and the
key CREATE/CALL/result/action event kinds, order, and ownership have the stated
projection. The universal equality closes the complete published opcode-layer
transition around explicit oracles; it is not a proof of the arbitrary child
program, oracle implementations, or concrete database implementation.

## Explicit premises and exclusions

World reads and mutations are represented by supplied world/journal tokens.
The claim assumes those tokens faithfully represent the admitted `IWorldState`,
`StackAccessTracker`, `VmState`, and code-repository adapter calls, including
snapshot, restore, transfer, create, destroy-list, and log effects. Account
balances are assumed to be valid 256-bit values. Access-list tracing prewarming
is represented in the pricing/journal projection; transaction-end access-list
enumeration is outside this opcode slice.

CREATE/CREATE2 address derivation, RLP, init-code hashing and Keccak, delegation
discovery, code-cache behavior, runtime-code validity, precompile execution,
and arbitrary child execution are explicit oracle/premise boundaries. No claim
is made about their implementation, hash/cache equivalence, concrete trie or
database journals, pooled/unsafe memory, CLR/JIT/AOT or native behavior,
function-pointer dispatch, tracer callback implementation or exceptions,
cancellation delivery, metrics, transaction/block composition, hardware, or
zkEVM sources. Tracing callbacks are assumed total and non-throwing.

Trace refinement does not claim capability-dependent payload equivalence.
Stack and memory report bytes, return-data reports, and action value/from/to,
input, execution-type, and precompile payloads remain open adapter premises.

`TryReserveChildGas` failure after CREATE's pre-reservation trace end is
unreachable only under the admitted standard Amsterdam EIP-150 natural-number
model; this package does not generalize that fact to arbitrary gas policies.
The current production CREATE trace ownership marker is included: CREATE keeps
its pre-reservation gas report, generic dispatch and suspend closure skip a
duplicate, and parent resume retains the post-child result report.

## Reproducible checked evidence

The corrected package regenerates 7 opcode descriptors, 28 exact closed roots,
79 C# source identities, 4 raw-source identities, 105 exact Roslyn admissions,
and 14 semantic bindings. The checked artifacts are:

- IR: `b74a9454441980789ee625402d8d37dae750c7767614e100f5502c20208b3aa5`;
- source manifest: `a8ad6c834d521ba0b55e3ef28ce994663c0099ba78046d5970830cd11a526164`;
- theorem-free generated Lean: `180665faeb11ec96337392b66906ad8e2d04cd6536d2ae22d977c57acc13def7`;
- combined admitted source closure:
  `4d0d21ed242dedb22cd340e8c82dc82da04df632ab3d12904ff5c52e9cd608b6`.

Both extractor projects build with warnings as errors, all 41 discovered MTP
tests pass, the 14-job package Lake build and 4 direct warning-as-error Lean
checks pass, formatting is clean, and two fresh extractions are byte-identical
to each other and all three checked artifacts. Independent review is pending.
Even after acceptance, this package will not establish the enclosing frame
loop, arbitrary child execution, precompile algorithms, or transaction/block
processing.

# Source, proof and executable regression gates

The source gate has executed compile-valid mutations with exact named semantic rejection, including false/dynamic guards, branch polarity, target/order changes, refund overload selection and fee/receipt argument substitution. The independent handwritten vectors also compile nine deliberately wrong semantic alternatives and prove each differs on a concrete case. Those finite witnesses are not yet mutations of generated artifacts. Every source mutation must compile successfully against the selected baseline compiler closure before rejection; parser/type failures are not successful evidence.

The complete gate includes 13 actual generated-definition mutations: rollback predicate, frame/floor gas inputs, REVERT halt routing, gas-field projection, counter prefix, fee formulas, caller-copy preservation, receipt-prefix identity, REVERT output, disposal order and maximum-used gas. Every changed definition must compile warning-free before a concrete expected-value equality is required to fail. The gate also requires all 46 C# tests to succeed without skips, with four synthetic summary controls; it checks all 131 dependency identities, 866 artifact-schema controls, and the frozen 42-module/13,336-declaration standard-only census with 17 controls. Passing these checks does not discharge the published external obligations.

## Source controls

1. Replace frame-gas copy with intrinsic gas; swap refund floor/standard arguments; replace prepared reservoir baseline with post-VM reservoir; zero delegation count.
2. Replace rollback OR by AND; include REVERT in the exceptional halt branch; replace VM REVERT entry baseline with zero; swap reset/clear cleanup.
3. Restore the wrong snapshot; move RIPEMD restoration before state restoration; redirect same-typed environment, sender, executing account, transaction or header.
4. Charge fees from effective block gas instead of spent gas; change minimum base fee to maximum; invert free, beneficiary or collector guards.
5. Move deferred destruction across fees or weaken its guard. The empty-destroy domain does not itself distinguish every such change; exact source admission must reject changed out-of-domain control rather than invent a semantic counterexample.
6. Swap spent/effective gas in processor or receipt accounting; replace header maximum by sum; weaken the effective-gas zero-execution/nonzero-state condition.
7. Derive receipt failure only from IsError, erase REVERT output, forward VM logs on failure, or substitute the wrong current receipt index/prefix.
8. Introduce local/delegate/lambda Refund or PayFees shadows; direct/aliased/inherited Conditional; async-void PayRefund; partial/extern/iterator alternatives; same-typed parameter-role swaps; receiver and implicit/narrowing conversion changes.
9. Alter standard/zkEVM symbols, selected tree/reference roster, reference MVID, duplicate selected trees, callable overload resolution or callback re-entry assumptions.
10. Change an upstream artifact, own specification/refinement/schema/script dependency, source identity, generated artifact path, hash, nullability or unknown property; require deterministic rejection.

## Reference vectors

- Success, REVERT and each admitted exceptional status constructor, including nonnull failed receipt error while TransactionResult.Error remains None.
- Nonzero entry state usage from a dead-recipient charge; child state creation cleared on REVERT; distinguish frame entry baseline from intrinsic baseline.
- Refund caller-copy preservation; authorization refunds; exceptional cleanup; zero/exact/partial state reservoirs; all six consumed fields.
- Zero execution with nonzero state, execution bottleneck, state bottleneck and zero both; unequal initial processor/receipt prefixes proving both header writes are modeled.
- Free transaction, zero/nonzero priority price, collector absent/present, blob collection, fee-report payload and UInt256 multiplication boundaries.
- Receipt output/log/error/root projection, sender/recipient/header/current index identities, normal callback order, frame/environment/access disposal.
- Signed negative gas, ulong boundaries, overflow-by-one and invalid identity/option/frame cases reject the domain rather than disappearing through Nat conversion.

Expected vectors must be independently literal or use a separately justified mathematical formula, never compare a function with itself. Four current constructive internal-domain witnesses establish satisfiable preparation/frame/range attachments. Their extension to full domains retains the actual external-provenance predicates; no arbitrary true oracle substitutes for production execution. Production reachability remains a separate obligation.

## Proof/export gates

Compile the exact generated/refinement/vector targets with warnings as errors. Check the frozen compiler-discovered module-origin inventory, including private and descendant declarations of every kind, and transitive Lean.collectAxioms with only standard axioms. Controls cover missing/unexpected exports, forbidden axiom dependencies, indented descendants and stale imported artifacts. No sorry, admit, extra axiom, native_decide or opaque escape is permitted.

Run semantic mutants only after each mutant compiles. Build Evm.slnx Release with warnings as errors and SaveDiskSpace, then rerun all affected live source checks. Independent static review precedes generation; independent executable review precedes any accepted status or root gate integration.

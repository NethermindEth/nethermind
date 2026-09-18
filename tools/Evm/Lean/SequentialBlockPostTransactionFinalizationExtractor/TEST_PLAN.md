# Test plan

Schema 8 / extractor 1.14.0 passed the serialized focused NUnit suite after warning-as-error
builds of the extractor and test project: 604 executed, 604 passed, zero failed or skipped.
`Verify.ps1` enforces the 604-test floor. The non-parallel suite covers:

- checked source/IR/manifest shape, exact helper commit flags, CFG/dataflow identities, and emitter determinism;
  method dataflow remains method-wide; event evaluation, optional invocation, and return-value
  positions bind their lowered operations rather than enclosing statement spans. The outer tail
  try is distinct from the finally's nested observation try, whose catch entry is recorded as an
  exception-handler region entry. Missing, redirected, or duplicated handler entries fail closed;
- unchanged-source normal-return dominance excludes disconnected handler roots without dropping
  the independent handler-connectivity audit. A small graph tests both a disconnected handler and
  a genuine ordinary entry-to-handler bypass. Serialized entry-to-return and entry-to-catch
  bypasses invoke the dominance validator directly so an earlier digest failure cannot mask them;
- exact `header = block.Header` origin and all seven preserved-value identity ledgers, with 48
  ordered read/receiver contexts. Sixteen compile-first source controls cover header cloning,
  initializer redirection, ref/in/out, aliasing, block/spec/tracer/handler replacement, world-state
  aliasing, and parameter rebinding in each of the four exact helper bodies. Coordinated header
  binding/ledger, helper member/CFG body, and other parameter/receiver IR redirections must reach
  the named identity gate;
- complete 14-helper preservation roster, including transitive reward calls, the standard partial
  receipt predicate, and admitted lambdas. Twenty-six additional compile-first source controls
  cover handler/tracer rebinding, aliases and ref escapes, receipt `Index`/`Logs` mutation through
  direct and aliased references, receipt-array element replacement, and indirect external array
  mutation. Six coordinated body/digest IR controls cannot redefine the preservation baseline;
  omission, duplication, reordering, path, and symbol redirection fail the exact closure roster.
  The typed five-tree gate retains only the one exact pre-tail handler-selection assignment;
- the five implicit timer sink callbacks and getter/type-initialization routes. Thirty-five
  compile-first controls cross each sink with callback/getter re-entry, a static constructor,
  an initializer helper, a captured processor field, an added conversion, and disposal. The
  re-entry variants capture the processor/spec in `ProcessOne` and reach the already admitted
  `CommitState` helper, avoiding a new direct-commit ledger row. Six coordinated declaration/digest
  artifact controls and nine roster/callback/site/wrapper binding controls cannot redefine this
  baseline. The unchanged boundary test covers all five sinks and their exact helper sites;
- eight compile-first generic activation controls cover `default(MetricsTimer<ExtraSink>).Dispose()`,
  ordinary/target-typed constructors, constructor-then-dispose, a default local receiver, reuse of a
  known sink outside its admitted site, and nested source-bearing generic and tuple arguments. Six coordinated
  activation/sink artifact controls reject extra constructor/default routes, owner redirection,
  omitted and duplicate entries. Two compile-first dormant-helper controls reach the independent
  whole-tree timer activation gate without an earlier reachable-call failure. `Verify.ps1` enforces
  the complete current 604-case floor;
- fourteen compiler-support/alias controls cover the exact existing metrics alias, the separately
  pinned four-tree support roster, support overload shadowing, direct and callback re-entry, and
  support/semantic-tree misclassification. Compile-first support mutations reach both the fixed
  byte pins and the independent typed re-entry boundary; support bodies are not verified merely
  because they are compiled in the real same-assembly model;
- complete entry-body preservation closes all additional metadata callback sites. Six compile-first
  controls pass the processor directly/as an alias/as a nongeneric comparer to `Array.Sort`, pass it
  to a metadata collection constructor, and pass its method group/direct delegate alias to sorting.
  The source comparer invokes the admitted commit helper through a captured processor/spec.
  Three coordinated body/digest artifact controls cannot add calls, constructors, or interpolation;
- standalone `--check` uses the extraction admission derivation, with no artifact writes. Five
  compile-first controls coordinate changed source bytes, valid source-pin overrides, IR source
  identities, and manifest hashes while leaving stale entry/helper/sink/activation evidence.
  Each reaches its named live-source gate; a harmless source-offset change reaches the complete
  rederived-IR equality gate. Test overrides isolate these gates after unchanged upstream admission;
- exact `Nethermind.Evm.Tracing.IBlockTracer` identities in ProcessOne, ProcessBlock, and miner-reward
  sites; a compile-first explicit type reference to a same-name interface in the wrong namespace fails both
  tail and suffix admission, and namespace-only IR identity mutations fail independently;
- three-way commit event/outer-call/helper mapping (`commitNoRoots(0)`, `commitNoRoots(1)`, `commitRoots`),
  global typed commit enumeration, direct-commit diagnostics, duplicate roots after `finally`,
  helper-equivalent commit calls, compile-valid `FlushState`/`RewriteHeader` source-local closure
  diagnostics, nested helper-type commit/hash closure diagnostics, and a compile-valid same-source
  delegate-field header write rejected with the dedicated un-audited-delegate diagnostic, and a
  compile-valid same-source property-getter header write rejected at its typed accessor;
  delegate property/accessor, lambda, and method-group invocations remain fail-closed, with source
  property/event accessors rejected rather than double-walked as declaration-time execution, and the
  exact typed outer-`ProcessBlock` `TransactionsExecuted?.Invoke()` event hook as the only admitted delegate invocation;
  a callback inserted into an opaque helper is rejected,
  including unguarded CFG reachability and dominance over post-transaction commit, plus separate
  subscriber/commit normal-return premises;
  compile-valid source-owned constructor/object creation, interface dispatch, binary operator,
  user-defined conversion, and dynamic-member sentinels fail at their dedicated local-closure
  diagnostics. Function-pointer/address, source accessor, implicit `using`, and custom await edges
  are fail-closed; only the exact typed `CountLogs`, `CalculateBlooms`, and `AccumulateBlockBloom`
  loops are admitted. Nested callable bodies are audited only through exact admitted callable edges,
  including the `CalculateBlooms` parallel lambda; opaque-hook helper calls and nested local
  functions fail closed;
  typed selected-arm `BlobGasUsed`/synchronous `ReceiptsRoot` writes, excluded background tuple
  write, and rejection of duplicate false-arm account/state calls or later header overwrites;
- typed `ComputeStateRoot` and `SetAccountChanges` helper-member/CFG bindings, exact value symbols,
  global state-root/account-change/hash write audit, and direct/deconstruction/compound/ref-like,
  competing, duplicate, and later-write diagnostics;
- actual `SequentialBlockTransactionFold.FoldResult` bridge fields, same-count/different-receipt rejection, elementwise projection mutation rejection,
  and the source-attached generated/reference relation's adapter, no-additional-effects, and post-commit proof fields;
- deletion, duplication, and reordering of the three `CommitState(spec)` calls;
- swapping `commitRoots:false` and `commitRoots:true`, or rebinding either helper;
- inverted EIP-4844, background, main-thread, and state-root guards;
- `CalculateBlobGas`, receipt-root, execution-request, and `EndBlockTrace` argument swaps;
- rewards/withdrawals/requests/trace/root/BAL/hash order mutations;
- BAL-before-hash inversion, aliases, early returns, and the existing nested bloom-lambda
  `return receipts` being excluded from the outer return binding;
- IR mutations to operation, symbol, CFG endpoints/order/back-edges, duplicate or redirected bindings,
  block-span membership, reachable-ordinal redirects, guard polarity, commit flags, steps, bridge
  fields, and result observables;
- vectors for 15/16 receipts, 63/64 logs, empty/non-empty blocks, EIP-4844 on/off, main-thread and state-root toggles;
- compile-valid callback relocation into a conditional, lambda, or try/finally wrapper, duplicate callback, opaque-hook
  direct/helper-hidden effect, unbound opaque `Task.Run`, exact bloom-parallel lambda/body, and
  initializer source-call mutations;
- empty/non-empty header-bloom effects, task reassignment, ref aliases/ref-out, captures, indirect
  task uses, wrapper conditionals around each unconditional tail action, and each per-hook normal-return observation;
- a hook-throw vector that is explicitly unsupported because the theorem is normal-return only;
  callback-subscriber and post-transaction-commit normal-return toggles are independently unsupported.

Mutation extraction must fail before writing any artifact and must never alter the pinned
production source. The test plan does not claim that production C# or CLR behavior has executed.

## ProcessOne validated-publication suffix

The suffix tests remain in this package and non-parallel. Schema 2 / extractor 1.0.0 is an
independently accepted conditional source-audited refinement. Admission binds eight semantic
source trees, four separately pinned compiler-support trees, strict IR and source-manifest
schemas, and exactly 52 dependencies. Default `--check` validates finalization only;
`--publication-check` independently rederives the suffix IR from live compiled source and validates
its artifacts. Draft-marker rejection remains a negative control, not the expected accepted-artifact result.
`Verify-Publication.ps1` runs the shared gate with its current 604-test floor, two deterministic
suffix extractions, the warning-failing Lake target and four direct Lean targets, fifteen semantic
mutations (eight publication and seven finalization),
the full descendant-axiom census, and a final live suffix check. The complete gate passed and
independent executable review accepted its final source/artifact identities.

The compile-valid mutation matrix is:

- delete, duplicate, reorder, or redirect the ProcessBlock-result assignment, `processed=true`,
  either retry catch, finally disposal, validation call, store guard, InsertDeferred call, or
  returned tuple;
- invert/remove `NoValidation` or `StoreReceipts`, swap validator arguments, remove the `out`
  error symbol, or admit a BAL/parallel retry under the nonparallel premise;
- move disposal after `InvalidBlockException`, dispose on acceptance, omit rejection disposal,
  add PostValidation/store/return to rejection, or classify generic validator failures as invalid;
  a same-name extension in the processor namespace must compile and then fail the exact disposal
  target diagnostic, while both baseline anchors resolve to `BlockExtensions.DisposeAccountChanges(Block)`;
- delete/reorder/redirect each of the four PostValidation copies, replace encoded-BAL
  `processed ?? suggested` with overwrite/reverse fallback, or alter the source/destination field;
- remove or hide the validator mismatch `GeneratedBlockAccessList` write/observation, change
  proposed-before/after tracking, store before publication, duplicate InsertDeferred, or change
  its arguments;
- return the suggested block or a different receipts local, add CommitTree/reset/head effects,
  or alter any source/FQN/symbol/CFG/data-flow identity or source digest;
- mutate the acceptance marker, stale digest, dependency identity, IR/manifest
  artifact hash, ordered events, forbidden-effect list, or open-obligation list.

Promotion hardening also rejects same-typed parameter-role swaps, declaration/ordinal/conversion
drift, source-local helper shadowing, asynchronous/iterator/conditional helper execution, and
preprocessor/source-selection changes. Compiler-support identities remain distinct from semantic
ownership. Both artifact schemas require complete typed records and reject unknown properties.

A successful unchanged compilation and extraction baseline is mandatory before any mutation.
Every source mutation first passes a separate Roslyn compilation gate using the pinned metadata
inventory; a mutation must change source bytes and then fail admission at its named diagnostic, so a compile error or unrelated rejection cannot
masquerade as the intended rejection. IR mutations compare against freshly rebound source evidence.
The stale-marker regression creates a temporary draft set without weakening source admission.
The emitted template is tested for deterministic bytes and
its source-site list, and the vector module uses named theorem assertions rather than unused Boolean definitions. All execution of these gates remains in the serialized lane.

ReceiptTerminal dependency admission has an in-memory complete supported fixture and focused
pin/manifest mutations for wrong schema, missing/malformed/empty source arrays, missing/unexpected/
duplicate/null source entries, missing/malformed/mismatched hashes, unknown fields, duplicate JSON
properties, and malformed JSON. Jointly empty inventories, jointly omitted `TransactionProcessor`,
and jointly stale source hashes must fail independently of document agreement. Source/IR/Lean
file absence, changed bytes, wrong paths, and mismatched manifest hashes each fail closed. Fixtures
refresh only in-memory identities and never alter the checked artifacts.

The semantic vectors cover validation enabled/accepted, NoValidation, validator rejection with a
proposed GeneratedBAL side effect, validator/cleanup/PostValidation/InsertDeferred escapes, StoreReceipts
on/off, encoded-BAL present/null fallback, and the forbidden CommitTree/reset/head boundary. The
normal-return theorem must consume explicit exact-base, standard sequential, BAL-disabled,
nonparallel, ProcessBlock, validator, PostValidation, and InsertDeferred adapter premises; no
production runtime or whole-result equality premise is substituted. The adapter still supplies
block/receipt identities, artifact snapshots and flag values; the theorem does not establish a
concrete options-mask relation or composition with a computed finalization result. Escapes assert
only the known prefix and no final state. Receipt contents, external hook implementations,
runtime dispatch, CLR behavior, durability, and downstream branch/chain processing remain open.

## Proof and artifact negative controls

The accepted gate compiles the unchanged finalization kernel plus its complete specification,
refinement, and vectors before seven semantic mutations. Each mutated kernel must compile independently;
the corresponding full proof must then fail semantically, not because of parser, import, resource,
or typeclass failures. Eight publication mutations use `-IncludePublication` through
`Verify-PublicationMutationGates.ps1` as part of the accepted gate, for fifteen combined mutations.
The accepted finalization
1305-export descendant roster matches compiler enumeration:
485 finalization, 223 Fold, 407 Receipt, and 190 Eip accounting-lineage exports, including generated
theorems and excluding ProcessOne. All four direct finalization Lean targets and `lake --wfail` passed,
and transitive `collectAxioms` is restricted to `propext`, `Classical.choice`, and `Quot.sound`.
Missing/unexpected/duplicate exports, a nested theorem, a forbidden axiom, and an omitted export
exercise the axiom gate itself, with nested unexpected/forbidden controls under all 12 namespace
roots. The full frozen roster/hash and these controls passed executable verification.

The accepted publication audit extends that roster to 1599 exports: 294 ProcessOne exports
and all 1305 upstream exports. It enumerates every descendant under the four ProcessOne
generated/specification/refinement/vector namespaces, including compiler-generated theorems;
the declaration-only list is insufficient. `PUBLICATION_EXPORTED_THEOREMS.txt` is frozen at
SHA-256 `9af770169f8539bf833aa4519a07b6a52fd0bffad5d513d40546f9ba0bf72312`.
The standard-only transitive axiom and missing/unexpected/duplicate/nested/forbidden/omitted-export
controls passed for the expanded census, including nested unexpected/forbidden controls under all sixteen roots.

Artifact controls cover nested duplicate JSON properties, nulls, token-aware placeholder detection
without comment/string false positives, exact generated Lean re-emission, and atomic replacement
failure preservation for both slice names across IR, manifest, and Lean files. `PROMOTION_PINS.json`
records accepted ReceiptTerminal schema 9/extractor 1.9.2 and Fold schema 9/extractor 1.8.0 hashes;
`PUBLICATION_PROMOTION_PINS.json` separately pins accepted finalization. Acceptance additionally
required the full executable gate and independent review. Two independent fresh extractions match
each slice's checked-in IR/manifest/Lean bytes. Full verification and `Evm.slnx` WAE/both postcheck
logs and exact artifact hashes are recorded in `README.md`; the complete current gate is
`D:/tmp/formal-verify/process-one-full-604-first-20260916.log`.
Suffix artifact publication uses flushed same-directory temporary files and atomic replacement,
with the manifest last. Interrupted publication must fail the consistency check; the test suite
checks prior-file preservation on replacement failure.
None of these tests changes the supplied-FoldResult (not `Fold.run`), receipt index/log-count
(not log-content), external-effect, static-dispatch, or normal-return theorem boundaries.

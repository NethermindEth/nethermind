# Simple-transfer completion extractor

This package is a hash-pinned, audited model-to-model artifact for the ordinary standard-mainnet
`TransactionProcessorBase<TGasPolicy>.ExecuteSimpleTransfer` request boundary. Its input is a local
handoff bundle. The extractor binds the exact `Execute/6` and `PrepareSimpleTransferFastPath/4`
route that selects and supplies the simple-transfer call; earlier validation, gas purchase, and
nonce update remain external prefix premises. The settlement/state-charge artifacts are separate
semantic kernel imports used as typed model responses. The output is the
generated `SimpleComplete` model and a concrete `ReceiptContinuationInput` model. `Reference/` is
an independently structured handwritten model and `Refinement/` proves that the two model results
agree under the package's explicit numeric, kernel, access-set, and normal-return premises; transfer
topics use the generated scalar address projection, and only access-carrier enumeration is
quotiented.

The claim is deliberately narrower than production equivalence. Roslyn admits exact source
identities, resolved operation signatures, source order, CFG reachability, and the finite guard
surface. The emitted operation terms are closed formulas for the two models; they are not a
compiler for the bodies of `PayValue`, `Refund`/`PayRefund`, `ShouldRefundGas`, `PayFees`,
`FinalizeTransaction`, world-state forwarding, tracer dispatch, or callback exception behavior.
The source and dependency hashes therefore audit the boundary and its admission, not composition
of production implementations.

The checked-in schema-1 generated bundle predates these caller-admission changes. It is stale and
unaccepted until the serialized build/test/Lean lane regenerates and validates it; this package
does not establish a source-attached production refinement claim in its current state.

`STAGE_B.md` describes a separate executable candidate. It interprets the accepted
post-nonce CFG/local-call prefix for a deliberately bounded ordinary-transfer input slice and stops
before `Refund`; it does not change the generated model or this package's formal claim. Its address
values are opaque canonical identities, and its empty collection/buffer representation is an
observational quotient rather than a general CLR heap or byte/address representation.

The separate `StageB/Generated` path now serializes the complete compiled prefix into canonical JSON
and typed Lean program data. Its manifest binds the IR, emitted Lean, and Stage-B Syntax bytes, and
the `--stage-b --check` gate compares all supplied artifacts with fresh source-derived regeneration
under an independently pinned prefix digest. A total fuelled Lean interpreter executes those emitted
functions, CFG nodes, bindings, aliases, typed requests, kernel calls, and the exact pre-Refund
suspension over the bounded common domain documented in `STAGE_B.md`; fail-closed literal vectors
exercise the same global fuel bound as separate C# literal tests. A separate native Lean replay
executable and C# harness independently decode identical NDJSON inputs and compare complete typed
terminal observations, exact tape suffixes, twelve-operand suspension values, resolved alias identity,
full provenance and remaining fuel. This is finite differential evidence; C#/Lean interpreter
refinement remains open. The stale Stage-A `Generated` triplet is not touched by this path.

Run `Verify-StageB.ps1` for the focused Stage-B lane. It checks fresh source-derived artifacts, the
pinned ordinary-Refund adapter manifest and source closure, the standard-mainnet Refund-dispatch
artifact, warning-as-error C# builds, a fail-closed minimum of 556 discovered Stage-B tests, the
executable Lean vector target, direct Lean checks, and placeholder absence without invoking the
known-stale Stage-A reference target.

The candidate schema-2 extractor compiles one real Release `Nethermind.Evm` Roslyn closure using
the accepted Refund source and reference inventories: 157 source identities, 146 compiled trees,
and 226 selected metadata references. Its `sourceClosureSha256` covers the effective compiler
source/reference identities as well as the package's source projections; mutation candidates
record their substituted source hashes. Core, State, and Init bodies remain explicitly labeled
source projections or normal-return/runtime-route premises, not members of that EVM compilation.
The former empty VM adapter is removed; the real VM declaration is compiled.

The model records typed world/tracer/receipt request payloads, but assumes normal-return request
issuance at those boundaries. This is an explicit input domain: `TraceAdapter.normalReturn = true`
is required by `universal_refinement`; callback-exception prefixes are therefore not represented.
World reads/writes and before/after state-root values are opaque
adapter inputs. `ReportAccess` is interpreted extensionally as sets of addresses and storage
cells; live `HashSet` enumeration order is not part of the claim. Transfer topics are derived by
the generated boundary's scalar `addressHashProjection` from the source-bound sender and
recipient. Production `Address.ToHash().ToHash256()` zero-left-pads the 20-byte address to 32 bytes;
the current model does not yet prove the required `UInt160` bound or byte-encoding bridge. The
generated transfer-log request derives its signature/data/address fields from the source-bound
transaction/log constructor. The
`TransactionResult` view is the model's status/receipt projection, not the complete CLR result
including every exception or description field. Receipt folding and receipt-root calculation are
outside the boundary.

The source admission includes `PayValue` in its exact source-order sequence before the recipient
balance request, the EIP-8037 state-charge branch, the four no-frame `CompleteWithoutFrame` gotos,
and the `FailContractCreate -> Complete` bypass. The no-frame gotos are exact source-bound label
branches; `NoFrameCfgProven=false`, so no unambiguous CFG-edge claim is made for them. A source
mutation that changes an admitted guard, cost, overload, receiver, argument, order, or transfer-log payload is rejected or
changes the rebound model artifact.

The package pins the live transaction-processor source and all selected closure identities. Its
current audit records one confirmed production defect, fixed in the live source: the EIP-8037
state-charge OOG no-frame completion path omitted `ReportAccess`. No additional production defect
was confirmed by this bounded audit.

## Verification

The focused Stage-B checkpoint is documented in [STAGE_B.md](STAGE_B.md). Its independent
control-kernel proofs cover source-extracted fuel ticking, edge selection, block finishing and local-return selection in the
candidate interpreter, with local jump, nearest-resume/root Return, two-step local resume and the
three-step return/resume/caller-writeback transition
simulations preserving every unrelated Machine field. The compiler, C# runtime and Lean admission
now agree that operation-level Return nodes are unsupported; a recursive theorem and a kernel-checked
fact for the emitted program establish that admitted normal returns use CFG Return exits instead.
This local admission claim assumes the program graph remains frozen for the single-threaded run;
mutable array ownership and concurrent mutation are not yet proved.
The same leaf also extracts the transparent-wrapper read selector. Local runtime theorems derive the
generated parent Tick and exact one-child schedule, then preserve the child operand identity or
materialize one readable value exactly as selected. This covers emitted Argument,
DeclarationExpression and ExpressionStatement wrappers, not Conversion, absent Parenthesized nodes,
or the CLR operand/location representation bridge.
An exact `setLast` theorem and two-step composition carry the wrapper result through the outer C#
`Read` into the frame value consumed by `FinishBlock`. The theorem deliberately retains the runtime's
all-matching duplicate-frame-ID behavior instead of assuming a uniqueness invariant not yet proved.
Exact block-entry equations then bind generated fuel ticking to ordered operation/update pairs, the
optional branch/update pair, `finishBlock`, missing-block rejection and Outside-before-evaluation.
The source gate pins the concrete stored Function and entry-block projections. An ordinary-decide
fact proves unique function and per-function block keys plus all entry presences for the fixed
generated program, with exact generic list-lookup consequences. CLR dictionary behavior,
frame/function association and the CLR-to-Lean representation bridge remain explicit premises, so
malformed arbitrary-program lookup behavior is not claimed equivalent. The generic handwritten call
model now rejects immediate writable `ref`/`out` arguments, aliases every existing reference location
without reading it, creates readonly temporaries for the three readonly-reference kinds, and treats
descriptor-free operator arguments as positional value copies after the same arity check as C#; a
zero-parameter fallback still ignores extra operands. Local equations and a direct private-`Call`
probe cover those branches. Generic Lean theorems now prove exact successful allocation counts,
consecutive IDs, old-cell suffixes and preservation of prior successful reads, plus the complete raw
post-state and symbol lookups for an ordered reference/readonly-immediate/value-copy family.
Descriptor-free corollaries cover copied-cell counts and the zero-parameter no-cell case. These are
handwritten-machine results, not an arbitrary-list content theorem or CLR-to-Lean call-binding
refinement. Explicit all-by-value descriptors now separately prove one non-alias allocation per
descriptor, reverse raw-cell order and duplicate-safe last-binding lookup; unique selected symbols map
descriptor position `i` to `nextCell + i`. Frozen ordinary-kernel facts pin the local
`TransactionResult` order `0,2,1`, including its implicit default argument, and all eight ordered
`TransactionSubstate` parameters. These counts and lookups remain conditional on successful binding
and do not establish general payload equality or reachability. For the frozen three-argument
`TransactionResult` shape, a separate exact equation starts at the actual frozen initializer block,
performs its entry tick, evaluates the object-creation root, and composes the seventeenth microstep
with the admitted `0,2,1` binding. Its nine ticks fix the exact error, complete string and retagged
exception values; the caller-owned three-field default receiver; the callee-owned parameter cells;
the reverse raw-cell and frame-binding order; all counters; and the unchanged surrounding Machine
state, under explicit frame-presence, function-association, fresh-frame-ID and fixed-width fuel
premises. An eight-tick
initializer theorem also exposes the unchanged heap and allocation counters after frame normalization
immediately before fuel exhaustion, and an old-cell-bound theorem excludes collisions between the four
new IDs and pre-existing cells. The repeated-call private probe now invokes the real initializer
`Execute` boundary and uses reference equality to confirm distinct receiver, frame and parameter
objects while retaining the
caller cell and capture; it also checks exact default fields, nine ticks per call/18 across both calls,
remaining fuel 9 then 0 at entry, and pre-allocation failures with both seven and eight ticks. String
payload boundaries and compile-valid value/default mutations are covered independently. C# and Lean
represent the copied string as a textual reference quotient, not physical CLR string identity. These
are source-pinned generated-node semantics plus matched executable evidence, not an extracted
C#/CLR-heap refinement theorem. A lower-level equation now starts at the exact generated function-9,
block-3 value-mode static-field node: a cold cache reaches the same constructor entry in 19 microsteps
and ten ticks while leaving publication pending, and a readable cached cell returns in two microsteps
and one tick without allocation. The generic cached-value branch now rejects cell-read failure, with
separate success/failure equations. Over a canonical empty surrounding Machine, exact Lean equations
continue through all three constructor assignments, both returns and publication: 43 microsteps/18
ticks from constructor entry and 62/28 from the cold selected node. The final operand, five cells, one
static binding, two retained frames, counters and cleared return slot are explicit; 56/57-step companion
equations expose the fully assigned unpublished state and the 27-tick exhaustion. This does not yet
generalize to arbitrary old heaps, dangling locations or caller frames. The executable probe
independently checks exact 28-tick success, both distinct unwind values, parameter-cell stability,
publication only after normal initializer return, physical identity on the following one-tick cached
read, and no publication when 27 ticks exhaust at the constructor terminal block after its assignments.
The currently admitted public-input domain rejects this intrinsic-gas failure path, so these results do
not prove outer-call reachability. The selected `Ok` node on function 9's success branch has a separate
arbitrary-context equation: its childless default initializer completes in eight microsteps/three ticks,
adds one frame and one static cell, and preserves the old suffixes, tape and requests. Its description
is null, and its cached value returns in two microsteps/one tick without allocation. Two ticks reach
default-value evaluation with no publication. Cold completion requires a fresh frame ID; separate
successful old-cell/read preservation requires a fresh cache-cell ID and excludes failed reads and
dangling aliases. These are natural-number-fuel Lean equations: correspondence to the C# signed-long
candidate requires `residual + 3 <= Int64.MaxValue` for cold completion and `residual + 1 <= Int64.MaxValue`
for a cached read. A real-interpreter probe covers zero through four ticks,
normal-return publication, cache identity and unrelated caller/static/tape preservation; compile-valid
default/cache mutations fail admission. The strengthened `commonDomain` now implies all nine
initialization refinement obligations and the generated initialization kernel's success outcome, and
a separate theorem identifies all five gas fields with the independent natural-number model. Exact
generated-control equations cover function 5's block-0 entry in two microsteps/one tick and its
block-4/block-5/local-resume suffix in eight microsteps/three ticks over arbitrary surrounding state.
The real-interpreter probe composes the actual generated `CalculateAvailableGas` function with the
production initialization adapter: cold success takes 80 ticks, cached success takes 78, and 79/77 are
the respective one-short boundaries; it also checks out-cell alias/copyback and result/cache object
  identities. Exact generated equations close function 5's block 1 in 45 microsteps/21 ticks and block 3
  in 76 microsteps/34 ticks, preserving the result binding/cell, capturing and filling the new gas object,
  copying it through the out alias, and reaching the proven suffix. The composed
  `generated_tryCreateAvailableFromIntrinsic_exact` theorem executes the complete emitted success body
  from its prepared entry through local resume in 131 microsteps/59 ticks. Discharging the enclosing
  function-9 caller premises, proving dynamic public-entry reachability, and the C#/Lean/CLR heap bridge
  remain open. A separate exact prefix now starts at function
  9's block-0 entry, evaluates all four
  emitted arguments, allocates the actual five-cell helper frame, preserves the caller output alias chain,
  and reaches function 5 in 25 microsteps/13 ticks. The function-5 body theorem now accepts the caller's
  exact output cell and location, including an arbitrary alias chain, under explicit successful-read and
  successful-write equations; its canonical unaliased theorem is a specialization of the same proof chain.
  A separate bridge theorem now composes the function-9 prefix with that generalized body in 156
  microsteps/72 ticks. It assumes the exact newest-output-cell head split and successor counter,
  identifies the emitted output binding, and transports explicit successful read/write equations from
  the actual caller heap. The endpoint retains the helper frame carrying `true`, both captures, the
  object/result/five bound cells followed by the written caller heap, the pending function-9 block-1
  continuation, and residual fuel. The subsequent generated success continuation is now exact: cold
  `TransactionResult.Ok` initialization takes 22 microsteps/eight ticks and a readable cached `Ok` takes
  16/six, producing complete conditional function-9 paths of 178/80 and 172/78. The endpoint is expressed
  through `scheduleReturn`; separate equations distinguish a terminal root, a retained enclosing resume,
  and its following unticked local-resume step. C# signed-long correspondence additionally requires
  `residual + 80 <= Int64.MaxValue` or `residual + 78 <= Int64.MaxValue`. The 79/77 one-short boundaries
  remain executable-probe evidence rather than Lean corollaries. The unique generated caller in
  `Execute` block 29 is now exact from the real invocation `.eval` task: it allocates the caller's
  `gasAvailable` local, binds function 9 with the exact cell permutation `[n+1,n+3,n+2,n+4]`, and reaches
  the callee entry in 22 microsteps/11 ticks. Composing that prefix with the accepted conditional path and
  the following unticked caller-local resume yields 201/91 cold and 195/89 cached. The endpoint restores
  the original work tail, clears `returned`, prepends canonical `Ok` to the original operand tail, preserves
  requests and tape, and leaves the caller gas local observably written through the exact output alias.
  The theorem requires the actual caller bindings/reads, an absent output local, fresh prior frame/cell IDs,
  and post-body `Ok` readiness; duplicate caller IDs follow the model's all-matching normalization. C#
  signed-long correspondence requires `residual + 91 <= Int64.MaxValue` or
  `residual + 89 <= Int64.MaxValue`. No 90/88 one-short Lean theorem is claimed. A further exact theorem
  composes the enclosing assignment to `result`, the actual implicit Boolean conversion (26 microsteps/
  ten ticks), `setLast` and `finishBlock`. From condition evaluation it takes 234/104 cold or 228/102
  cached; from actual block-29 entry it takes 235/105 or 229/103. It stops with block 31 scheduled,
  before that block executes. The endpoint restores the operand tail, clears `returned`, preserves
  requests/tape, has the specified residual fuel, writes canonical `Ok` and modeled gas into the caller
  locals, and leaves the caller and both conversion frames carrying `true`. From starting cell/frame counters
  `n`/`f`, `result` occupies `n`, gas occupies `n+1`, and final counters are `n+15`/`f+5` cold or
  `n+14`/`f+4` cached. These conditional theorems require both locals initially absent and the explicit
  caller-read, freshness, post-body `Ok` and signed-long fuel premises. Lazy `result` allocation belongs
  to this Stage-B selected boundary; production assigned the local earlier. Public-entry reachability,
  the relation for that dead prior payload and inactive `TGasPolicy` defaults, raw CLR heap equality,
  and interpreter/pipeline refinement remain open. Block 31 is now exact independently and in composition:
  its generated null-pattern condition takes 11 microsteps/four ticks, while actual block entry through
  successor scheduling takes 12/five. A typed non-null address falls through to block 32 and null schedules
  the outside-domain block 33. `generated_execute_block29_to_block32_exact` composes the accepted block-29
  path with the non-null branch in 247 microsteps/110 ticks cold or 241/108 cached. The proof derives
  preservation of the original recipient binding and arbitrary successful alias chain through the fresh
  result/gas heap prefix, retains all-matching caller-frame normalization, and stops with block 32 scheduled
  before `ExecuteSimpleTransfer` receiver or argument evaluation. Production reachability must still relate
  the saved 20-byte `tx.To` value to the model's address payload, establish the preparation/code-lookup
  branch, and encode Stage B's authorization flag as `AuthorizationList != null` rather than the differently
  defined `Transaction.HasAuthorizationList` property. Existing source guards and mutation tests pin that
  nullness meaning; no production-to-Stage-B adapter exists yet. This checkpoint found no production
  Nethermind bug and does not establish full verification. Generated block 32 is now exact through local
  callee scheduling: the standalone binder fixes the 15 `ExecuteSimpleTransfer` cells and five readonly
  aliases, the emitted invocation takes 64 microsteps/32 ticks, and actual block entry takes 65/33.
  `generated_execute_block29_to_simpleTransfer_entry_exact` composes the path in 312/143 cold or 306/141
  cached and derives the by-value gas argument at the original `nextCell + 1`. Its endpoint queues function
  24 block 0 with the exact local-resume/`setLast`/block-completion continuation; it has not executed the
  callee entry tick, body, state effects, or later `Refund` suspension. The success roots require successful
  reads for all 15 argument locations, including the readonly aliases, plus the stated binding, freshness,
  frame and signed-fuel premises. No one-short exhaustion theorem is claimed. Production reachability,
  address/input representation, standard-mainnet dispatch, raw C#/Lean/CLR heap correspondence and the
  complete interpreter/pipeline refinement remain open. Function 24 entry is now exact through completion
  of its first `Metrics.IncrementEmptyCalls()` statement: the empty block 0 takes two microsteps/one tick,
  while block 0 plus the first block-1 operation takes eight/four under an exact matching unit-reply
  exchange. Composition from block 29 takes 320/147 cold or 314/145 cached, consumes and records exactly
  that request, leaves the callee carrying unit, and stops before the `tx.ValueRef` ref assignment. The
  request proves normal modeled invocation completion, not a concrete metric mutation; disabled metrics,
  enabled thread-selected atomic updates, the response adapter and public production reachability remain
  open. No one-short theorem is claimed for this prefix. The remaining function-24 block-1 initializers
  and EIP-8037 branch are now exact in 55 microsteps/24 ticks. They allocate a readonly `tx.ValueRef`
  snapshot plus an aliasing local and four Boolean locals, compare the sender with the actual callee
  recipient, and schedule block 2 when EIP-8037 is enabled. Composition from block 29 takes 375/171 cold
  or 369/169 cached and stops before block 2 executes. The outer theorem retains the signed-fuel bound;
  no one-short theorem is claimed. Production `_value` aliasing versus the modeled snapshot, canonical
  address equality, stable spec/tracer getters, metric-response correspondence, public reachability and
  the C#/Lean/CLR heap/interpreter/pipeline bridge remain open. Blocks 2 and 3 now have exact 5/2 guard
  equations: zero value and self-send schedule block 6 without an account query, while a nonzero distinct
  recipient reaches block 4 after 10/4. The exact block-4 root consumes one recipient-specific
  `IsDeadAccount` Boolean exchange in 13/6 and schedules block 5 for dead or block 6 for live. From block
  29 the totals are 380/173 cold or 374/171 cached for zero value, 385/175 or 379/173 at the nonzero guard
  endpoints, and 398/181 or 392/179 through the query. The queried root requires the canonical processor
  receiver and stops before block 5's gas charge. Provider cache/read bookkeeping, traced BAL effects and
  oracle truth remain outside the model, along with one-short/rejection theorems and later state changes.
  Block 5 is now exact through the generated local cost getter, local charge function and computed
  `.stateCharge` kernel leaf. Success takes 159/73 and sequentially writes all five fields of the callee's
  by-value gas copy; OOG takes 94/43, preserves that gas object and sets the OOG local. Both allocate three
  cells/two frames, consume no tape, return to the caller with `returned = none`, and only schedule block 6.
  From block 29 after a dead-account reply the totals are 557/254 cold or 551/252 cached on success and
  492/224 or 486/222 on OOG. `commonDomain` pins the cost to 183600. These exact fixed-width interpreter
  equations do not discharge production representation/reachability or the natural-number EIP refinement's
  `Represents`, `WellFormed` and no-overflow obligations; block 6 and all later balance/settlement effects
  remain open.
  Blocks 6, 7 and 8 are now exact in 5/2 each. Their short-circuit composition reaches block 14 for a
  self-send in 5/2 or for distinct-recipient OOG in 10/4, and reaches block 9 for eligible nonzero value or
  block 10 for eligible zero value in 15/6. Conditional premises require only the guards actually reached;
  the block-8 false edge retains its generated `enteringRegions = [2]` metadata. From block 29 through the
  accepted state charge, successful charge reaches block 9 in 572/260 cold or 566/258 cached, while OOG
  reaches block 14 in 502/228 or 496/226. No successor executes: balance effects, action tracing and OOG
  execution-gas clearing are subsequent obligations. The exact endpoints preserve cells, aliases, caller
  metadata other than `carried`, statics, tape, requests, returned value and allocation counters. These are
  still conditional generated-interpreter theorems, not the open CLR/heap/production refinement bridge.
  The non-tracing OOG suffix is now exact as well: blocks 14 and 16 each take 5/2, block 17 including the
  local `ClearExecutionGas` body takes 31/14, and the combined suffix takes 41/18. It retains one writable
  alias cell and local-call frame, clears only the by-value gas copy's execution-gas `Value`, preserves all
  four state-gas fields and the true OOG local, and stops with block 18 scheduled but unentered. The composed
  block-29 totals are 543/246 cold and 537/244 cached. Tracing-enabled action reporting, block 18 and later
  settlement, and production alias/reachability refinement remain open.
  With pinned EIP-7708 disabled, block 18 is now exact in 16/7: it initializes one direct logs cell to the
  generated typeless value `.null ""`, allocates no frame, and follows the exact `enteringRegions = [4]`
  edge to block 24 without entering it. The OOG composition takes 57/25 from block 14 and 559/253 cold or
  553/251 cached from block 29, while retaining the clear-call alias/frame, zero execution gas, all four
  state-gas fields and OOG=true. Enabled EIP-7708 logging, block 24 onward, CLR null/allocation semantics and
  the complete production refinement remain open.
  Blocks 24--26 are now exact through the pre-constructor capture boundary: block 24 is 41/17, either enum
  selector is 7/3, and their composition is 48/20 with block 27 scheduled but unentered. Captures 3--8 hold
  empty bytes, signed zero, typeless null, logs and two false values; capture 9 is the typed `OutOfGas = 4`
  or `None = 0` enum. Existing captures with IDs 3--9 are replaced and all others remain. The OOG route is
  105/45 from block 14 and 607/273 cold or 601/271 cached from block 29, with heap/gas/OOG/tape state
  preserved. `TransactionSubstate` construction, the logger getter, block 27 onward and CLR/production
  refinement remain open.
  Block 27 is now exact on the null-logs, non-reverting path. Entry is 41/22; function 22 executes its nine
  selected blocks and all modeled field writes in 135/61; and the unticked resume, conversion, assignment,
  `setLast` and finish take 5/0. The complete 181/83 theorem handles both exception-enum outcomes and stops
  with block 28 scheduled but unentered through `leavingRegions = [4]`. It retains exactly ten new cells and
  one constructor frame, gives the receiver and caller local equal completed structs, and preserves prior
  cells, gas/OOG/log state, statics, tape, requests and arbitrary tails. The empty journal remains an
  interpreter value, and the Lean/C# Stage-B default shapes differ before the constructor writes `Error` and
  `_logger`; non-null logs, reverting/error-decoding paths, CLR heap/allocation identity and production
  refinement remain open.
  The tracing-disabled block-28 edge is now exact in 5 microsteps/2 ticks. It reads the existing
  tracing local as false, replaces all matching caller frames with carried value false, and schedules
  block 32 without entering it while preserving cells, statics, captures, counters, requests, response
  tape, return state and arbitrary tails. Independent source/IR review and sentinel replays cover
  direct and aliased reads, duplicate frame identifiers and exact fuel boundaries. The tracing-enabled
  callback, `Refund` suspension/execution, production
  reachability and complete pipeline refinement remain open.
  Block 32 is now exact through its first two intrinsic-gas copies in 19 microsteps/9 ticks: 1/1 for
  entry and 9/4 each for `FloorGas` and `Standard`. It creates the floor cell at `nextCell` and the
  standard cell at `nextCell + 1`, installs standard-then-floor bindings in every matching frame,
  carries the standard value and advances `nextCell` by two while preserving the remaining machine
  state and arbitrary tails. `GetStateReservoir`, both `setLast` operations, `Refund`, block finish,
  production reachability and the CLR/heap bridge remain unproved.
  The next exact checkpoint executes `GetStateReservoir` and its paired `setLast` in 23/10, making
  block-32 entry through reservoir initialization 42 microsteps/19 ticks. Its endpoint has four new
  cells, one retained fresh getter frame, the signed reservoir value in the getter and every matching
  caller frame, `nextCell + 4`, `nextFrame + 1`, and unchanged statics, tape, requests and arbitrary
  tails. Work starts at `Refund` evaluation, which remains unentered together with its `setLast` and
  block finish. Production reachability and the CLR/heap bridge remain open.
  `generated_execute_simpleTransfer_block32_refund_suspends_exact` continues from that endpoint to the
  typed external `Refund` suspension in exactly 99 microsteps/48 ticks from block-32 entry, composed as
  42 + (56 + 1). The host call stays on the existing caller frame and carries exactly twelve
  receiver-excluded operands in ordinal order: transaction, header, spec, options, readonly substate,
  readonly gas, readonly opcode gas price, unsigned zero, readonly floor gas, readonly standard gas,
  signed reservoir and false. The new `spentGas` cell remains default and unassigned at the original
  `nextCell + 4`; the `Refund` body/reply, outer assignment, following `setLast` and block finish remain
  unexecuted. The theorem is conditional only on stated original-state frame, absence, freshness,
  binding, read, resolve, reservoir-field and fuel-bound premises, and its axiom census is `propext`,
  `Classical.choice` and `Quot.sound`. Production dispatch/reachability, the CLR heap/interpreter bridge,
  the `Refund` implementation and settlement, and the full pipeline remain open. No production bug was
  found at this checkpoint, which is not a claim that Nethermind or the full pipeline is formally verified.
  `generated_execute_simpleTransfer_block28_false_to_refund_suspends_exact` composes the
  tracing-disabled block-28 branch with that suspension in exactly 104 microsteps/50 ticks: 5 + 99
  microsteps and 2 + 48 ticks. Using original-state premises only, it derives the false carried value
  and applies all-matching replacement to every duplicate caller-frame ID before block 32. It reaches
  the same exact receiver-excluded twelve-operand suspension with `spentGas` still default and
  unassigned at the original `nextCell + 4`; the `Refund` body/reply, outer assignment, following
  `setLast` and block finish remain unexecuted. Its axiom census is `propext`, `Classical.choice` and
  `Quot.sound`. Production reachability, the CLR heap/interpreter bridge, `Refund` implementation and
  settlement, downstream execution and the full pipeline remain open. This composition found no
  production bug and is not a claim that Nethermind or the full pipeline is formally verified.
  `generated_execute_simpleTransfer_block28_false_to_refund_boundary_refines` adds a separate
  two-phase bridge to that exact 104/50 suspension and its twelve located, provenance-preserving
  operands. It maps the five `Runtime.Gas` fields exactly into the ordinary-Refund input, requires
  explicit observations over the same opaque transaction, spec and options objects for source fields
  absent from `Runtime.Input`, and depends on the accepted source-attached ordinary-Refund adapter
  premise. The resulting boundary encodes exactly `SpentGas`, `OperationGas`, `BlockGas`,
  `BlockStateGas`, `MaxUsedGas` and `GasRefund`, while the pure adapter theorem preserves the incoming
  caller gas. The suspension remains terminal: no `Refund` execution or reply, result assignment,
  `PayRefund` or other world-state effect, caller resume, block finish or later block is proved. The
  production reachability and CLR/heap/interpreter bridges remain open, so this is not verification of
  the transaction processor, the execution pipeline or Nethermind as a whole.
  `generated_execute_simpleTransfer_block28_false_to_standard_mainnet_refund_boundary_refines`
  further composes the exact Stage-B processor receiver value with either admitted sealed standard
  leaf: `EthereumTransactionProcessor` or the BAL `TransactionProcessor<EthereumGasPolicy>`. Neither
  leaf declares `Refund` or `PayRefund`; both resolve those slots to
  `TransactionProcessorBase<EthereumGasPolicy>`. The generated source attachment pins the exact
  containing-instance calls from `ExecuteSimpleTransfer` to `Refund` and from `Refund` to `PayRefund`,
  the source and reference closures, and the Stage-B prefix and ordinary-Refund upstream manifests.
  A finite runtime concrete-type observation relating that exact receiver value to one of the two
  leaves remains an explicit premise. Fresh extraction, deterministic artifacts and mutations of
  sealing/slot ownership, lineage, either call, IR, Lean or manifest bytes fail closed, and the focused
  lane requires at least 556 discovered Stage-B tests. This theorem does not cover
  `SystemTransactionProcessor`, Optimism or Taiko processors, DI or plugin selection, host execution,
  the `Refund` return or caller assignment, `PayRefund` effects, caller resume or downstream blocks.
  `generated_execute_simpleTransfer_refund_result_install_exact` now keeps the external-result boundary
  explicit. From the same original state, an independent 103-microstep/50-tick execution reaches the
  exact pre-call machine; the existing 104/50 terminal suspension remains separate. Dropping the call
  task and its thirteen operands exposes the retained caller continuation. If an external result is
  supplied and proved equal to the accepted six-field `RefundBridge.resultValue`, two interpreter
  microsteps and zero C# ticks assign it to the existing `spentGas` cell at the original `nextCell + 4`,
  perform the following `setLast`, and leave block 32's `finishBlock` pending while preserving allocation
  counters, statics, tape, requests and returned state. The unchanged installed machine still rejects its
  next microstep with `"missing suspension"`. A separate source-admission theorem pins the exact four-operation
  source block 32, its regular edge entering region 5, empty source block 33 and its `WhenFalse`
  `newAccountOutOfGas` branch, plus region 5/capture 10 metadata. An explicit rearm changes only the pending
  finish task. Starting from the exact result-installed state with `fuel + 1`, the rearmed block-32 finish and
block-33 entry take two further interpreter microsteps and one C# tick, leaving guard evaluation pending and
residual `fuel`. A second exact source admission pins both block-34/35 `FlowCapture` terms, their static
byte `StatusCode.Failure = 0` and `StatusCode.Success = 1` fields, capture 10's explicit value mode and
the regular edges to block 36. From entered block 33 the readable Boolean guard, `WhenFalse` selection,
selected byte capture, `setLast` and finish take 11 microsteps/four C# ticks. From the exact installed
state indexed at `fuel + 5`, explicit rearm composes this suffix in 13/five and leaves block 36's entry
scheduled with capture 10 and the caller's carried value equal to the selected byte status. The interpreters
now model block 36's implicit byte-to-Int32 conversion and zero-initialized `int` local explicitly. From the
scheduled block-36 entry, declaration, conversion, assignment, `setLast`, and the regular edge take 11/five;
the composed entered-block-33 and explicit-rearm boundaries are 22/nine and 24/ten respectively. The exact
endpoint schedules block 37 with `statusCode` and the caller's carried value equal to `.enum "int" 0` on OOG
or `.enum "int" 1` otherwise. Capture 10 remains in the interpreter frame after the source edge leaves region
5; that is administrative interpreter state, not a CLR lifetime-cleanup theorem. The phases remain separate:
103/50 to the pre-call boundary, 2/0 to install an externally supplied result, and 24/10 after explicit rearm.
This is not uninterrupted execution and proves neither the host `Refund` call nor `PayRefund` balance effects.
`generated_execute_simpleTransfer_refund_accessGuard_source_admitted` pins block 37's exact
`tracer.IsTracingAccess` property, parameter receiver, empty operation list, `WhenFalse` condition, and
regular edges to blocks 38 and 39. `generated_execute_simpleTransfer_postRefund_accessGuard_exact` starts
with block 37 scheduled and proves exactly seven interpreter microsteps and three C# ticks under explicit
frame, receiver-binding/read, and fuel premises. It normalizes matching caller frames, sets their carried
value to the independent access Boolean, and schedules block 38 when true or block 39 when false. Existing
cells and the selected frame's captures remain unchanged at this guard endpoint.
`generated_execute_simpleTransfer_postRefund_call_frontiers_source_admitted` pins both complete successor
blocks, their exact receivers and argument passing modes, block 38's regular edge to 39, and block 39's
`FinalizeTransaction` return expression and region exit.
`generated_execute_simpleTransfer_postRefund_callFrontier_exact` evaluates the receiver and arguments in
21 microsteps/12 C# ticks from scheduled block 38, or 49/26 from scheduled block 39. It stops immediately
before applying `ReportSimpleTransferAccess` or `UpdateHeaderGasUsedAndPayFees`, with caller cells/captures
preserved under matching-frame normalization, readonly argument locations retained without payload reads,
and the caller continuation still pending. Explicit premises establish receiver and value reads, argument
bindings, frame identity, and sufficient representable fuel.
`generated_execute_simpleTransfer_postRefund_accessGuard_to_callFrontier_exact` composes from scheduled
block 37 in 28/15 when access tracing is enabled and 56/29 when disabled, retaining the guard's carried
Boolean. Two exact-source cases and fifteen compile-valid source mutations cover the new boundary.
The frozen replay still ends at `Refund`; these are separate residual execution theorems.
The fee helper's actual eleven-parameter signature and complete source CFG are retained as inert
source evidence, while a separate residual program admits only its empty entry block.
`generated_postRefund_feeHelper_entry_source_admitted` pins that entry and exact signature;
`generated_postRefund_feeHelper_bind_exact` and `generated_postRefund_feeHelper_cell_layout` establish
six by-value copies, five readonly aliases, exact receiver, ordered parameter identities and consecutive
fresh-frame cell identifiers. `generated_postRefund_feeHelper_call_entry_exact` advances the false-access
fee-call frontier by two microsteps/one C# tick, leaving the empty entry's `finishBlock` pending before
the guard. Caller continuation, old cells, the prepared caller frame tail, statics, host requests and response tape are
preserved; explicit fresh-cell bounds also preserve all existing successful reads. Ten tests cover
source admission, compile-valid source mutations, and re-signed inert-metadata controls.
An additional, separate source-admitted residual in `StageB/FeeHelper.lean` now covers the sequential
EIP-8037 counter/header path and stops with the exact ten-argument `PayFees` invocation fully evaluated,
its apply task pending. `FeeHelper.selected_source_admitted` (in namespace `StageB`)
checks the exact emitted helper CFG: routing bit-test and parallel guard, fork guard, execution add,
state add, header maximum after both writes, then the virtual fee call. The residual uses existing
source-pinned routing and receipt-accounting leaves, including unchecked UInt64 addition; a separate
no-wrap theorem refines those fields to natural sums/max. A fresh `--stage-b-effective-block-gas`
leaf admits the current getter, including zero execution with nonzero state. It does not import or
regenerate the stale Stage-A completion bundle; that bundle's drift is verification-artifact drift,
not a production bug.

Composition is only through `FeeHelper.EntryWitness`: the accepted helper-entry machine plus explicit
read/alias observations and external mutable processor/header projections. `entry_and_residual_compose`
joins the checked two-step entry with seventeen specialized residual steps, not seventeen generic
interpreter microsteps or C# ticks. Six by-value fee arguments and four readonly aliases retain exact
source order and provenance. The generic entry machine, request tape and caller continuation remain
unchanged while a separate projected write ledger records execution/state/header updates. Exact and
one-short fuel, wrap boundaries, max directions, bit masks, paid-versus-effective gas, and nested
readonly aliases have executable vectors. Twenty-two additional C# cases cover compile-valid source
mutations, current narrow leaf admission, and artifact/hash/roster rejection.

The access-report helper body, tracker/pool/callback behavior, concrete provider `PayFees` effects, `FinalizeTransaction`,
later transaction or pipeline processing, the concrete tracer-getter/CLR bridge, and uninterrupted
composition with the existing explicit-rearm/status-code phases remain open.
The new residual does not derive the reference heap or execute `CompoundAssignment` through ordinary
`Runtime.steps`. That bridge, mutable property accessors, skipped-counter/EIP-8037-disabled paths,
and `PayFees` virtual dispatch remain open. The frozen replay still stops at `Refund`.
`StageB/PayFees` now supplies a separate source-admitted projected continuation for the standard virtual
`PayFees` body. A fresh narrow extractor pins both ordinary sealed receiver lineages, the exact typed
body and signature, and the separately excluded empty system override. It neither imports nor changes
the broad `OrdinaryEvmCompletion` or stale Stage-A completion bundles. The reference uses bounded
256-bit inputs, exact modular products/sums, the effective-base-price minimum, free-transaction and
collector gates, and the precise tracer payload (including blob fees independently of collector gates).
Zero premium still emits the beneficiary credit/create call; same-account beneficiary/collector credits
remain two ordered calls. Repeated property reads have separate observations rather than an implicit
object-immutability premise.

Ten projected stages reach void completion with exact/one-short fuel proofs. `EventExecution` requires
every read at its actual interleaved world-state point and every abstract credit/report effect in order;
it imposes no provider, account-existence, balance, creation or journal law. An explicit machine
projection/noninterference witness bridges normal projected completion to the retained caller
continuation. Three checked generic-runtime steps resume void, clean up the statement, and `setLast`,
leaving the exact `FinalizeTransaction` invocation scheduled for evaluation. The following generic
argument theorem now evaluates its receiver and twelve arguments in exactly 51 microsteps/26 C# ticks,
or 54/26 including return/cleanup, and stops at the pending invocation application. Ten arguments are
by value; only payment ordinal 7 and substate ordinal 9 retain readonly locations, without reading
their payloads. The complete six-field `GasConsumed` value is copied. Arbitrary tails, duplicate caller
IDs, cells, frames, statics, tape, requests, allocation counters and returned value are preserved by
argument evaluation. No finalization body is entered or applied. Concrete DI resolution, plugin/system receivers,
UInt256 limb/CLR implementation, provider/tracer correctness and exceptional host effects remain open.
Twenty-four new C# cases and nine Lean vector groups cover the bounded continuation.

Fifteen additional compile-valid/source-projection controls and eight Lean vector groups cover the
finalization frontier, including 50/51 steps, 25/26 ticks, unreadable chained aliases and exact sentinels.
The separate `--stage-b-finalize-entry` artifact now admits the actual private, instance, nonvirtual
twelve-parameter signature, exact caller descriptor, empty reachable Entry0 and its regular empty-region
edge to block 1. Its purpose-specific residual contains only Entry0. The genuine generic binder allocates
ten copied cells and two readonly aliases (ordinals 7 and 9), with consecutive identifiers, exact symbols,
receiver, reversed raw-cell/binding layout and preserved old suffixes. Binding takes one microstep and
zero ticks; empty entry takes a second microstep and one tick. Composition gives 53/27 from the scheduled
invocation and 56/27 from the accepted void-return handoff. The endpoint has `finishBlock` pending;
neither the Warmup guard nor any finalization body/property/world/receipt effect has run.

Twenty-eight new C# controls and eleven executable vector groups cover this boundary, including
unreadable chained aliases, ten distinct abstract copied payloads, all six gas fields, duplicate caller
IDs, old reads, exact/one-short steps and fuel. Abstract payload sentinels test the binder, not a C#
type/heap representation theorem. Frame/cell freshness and representable fuel remain explicit.
The Stage-B gate checks six extraction modes, at least 584 tests and seventy-five exact roots transitively for nonstandard axioms, with
malformed-output and injected-axiom negative controls.
  Generic duplicate-key
publication, arbitrary static-node children,
CLR whole-type initialization ordering/locking/reentrancy/failure poisoning/value-copy semantics,
general value/default and heap correspondence, C#/Lean constructor refinement, and non-local object
creation remain open. Malformed explicit
descriptor/index failure ordering, complete interpreter simulation and production-pipeline refinement
also remain open. The handwritten
assignment branch now follows C# by
installing ref aliases without a source read and rereading value-assignment sources after the target
write; universal equations fix both orderings. Cross-language composition must still prove that a
ref-assignment target denotes a concrete C# `CellLocation`, rather than a Lean readonly-temporary or
discard root that also has an empty field path.
An ordinary-decide closure fact now checks every one of the frozen program's 768 nodes, 23 local
calls and 47 Invocation/ObjectCreation descriptors for target existence, callee-before-caller order,
receiver/argument/parameter bounds, per-function parameter-symbol uniqueness and descriptor-free
positional arity. Generic Lean equations
derive successful frame creation, exact local apply scheduling and first-block success, zero-fuel
and Outside transitions while preserving unrelated execution context. Exact Call/Target/Argument/
Member/Parameter record and enum shapes are source-admitted, and an 18-observation recursive C#
probe executes the real callee and caller continuation with seven behavior mutants. The concrete
Cell/Operand/Location/StageBValue representation declarations and their semantic operation targets
are now independently pinned, with compile-valid mutations and alias/copy/readonly runtime
observations. In particular, C# defaults `TGasPolicy` to null where Lean constructs zero gas, and the
default C# `TransactionSubstate` omits the `Error` and `_logger` fields present in Lean. C# discard
writes are no-ops while the Lean model uses a writable cell; proving the frozen discard sites
unobservable remains part of the future reachable-heap relation. The exact frozen constructor-entry
result now includes child evaluation, but still does not prove arbitrary-list parameter contents, the
general CLR heap representation, dynamic reachability from the complete caller, malformed failure
order, complete callee execution/unwinding or whole-interpreter equivalence.
`Verify-StageB.ps1` requires Release, checks both generated artifact bundles, builds those leaf
proofs and the shared replay executable, and runs all focused Stage-B regressions/mutations.

Run `Verify.ps1` from the serialized build lane. It builds the extractor and tests with warnings as
errors, regenerates into a temporary directory, checks deterministic checked-in artifacts, runs
the complete discovered test count, and typechecks the four Lean targets under the pinned
`leanprover/lean4:v4.33.1` toolchain. The editing lane does not invoke these commands.

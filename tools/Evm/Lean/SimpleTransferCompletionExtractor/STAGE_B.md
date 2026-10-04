# Stage-B lowering checkpoint

`StageBLowering.Build` constructs a separate, internal executable-plan representation from the
pinned live Release `IOperation` and CFG compilation. It does not interpret `StageAPlan` or its
`TypedAst`, change public extraction, emit Lean, or regenerate the checked-in `Generated` triplet.
This is not an accepted production-refinement result.

## Selected route and ownership

The route starts after successful nonce update in `Execute/6`, retaining the source-derived
restore/commit expressions, recipient/code guard, available-gas adapter, and simple-transfer call.
The terminal scope is ordinary Ethereum, sequential Commit, receipt tracing, no state tracing,
and EIP-658. Prefix/fallback, restore/noncommit, and reverting-substate regions are explicitly
marked `OutsideSelectedDomain` in the original CFG; the executable statement slice does not
silently erase arbitrary operations. Scope-input writes and writable local aliases fail closed.

Locally lowered bodies include preparation/guards, initialization and state-charge copy-back
adapters, simple transfer, value forwarding, action/access tracing and warmup, gas/header/fee
updates, selected finalization, gas/counter accessors, world-state forwarding, transfer-log
construction and static initializers, the non-reverting substate constructor/projections,
result construction/conversions/projections, exception rendering, and tracker warmup/disposal.

Methods, constructors, properties, fields, user-defined operators, conversions, and implicit
foreach calls have resolved ownership. Static policy dispatch uses Roslyn's exact interface
implementation lookup. Only exact resolved entries map to the four accepted stages:
initialization `TryCreate`, state-charge `TryCharge`, ordinary `Refund`, and the receipt
`MarkAsSuccess`/`MarkAsFailed` terminal entries. Each binding records its theorem interface and
source-member dependency closure. Candidate changes to these source dependencies fail rather
than silently changing an imported stage. The closure compares baseline/candidate operation bindings,
including assembly-qualified recursive type/member identities, constants and enum values,
conversions, argument/default bindings and declaration-relative local/label identities. Unchanged
method text cannot silently bind to a new nested helper. Receipt implementation dispatch still requires its
external standard-mainnet provenance and later dependency/artifact composition gate.

Other admitted members carry a typed request category and complete resolved signature. The
boundaries include world/tracer calls, code lookup/sentinel representation, metadata/value
projections, pool/collection operations, UInt256 arithmetic, and address/hash/byte primitives.
Source-backed external declarations are compared with the pinned baseline; metadata references
retain the accepted compiler inventory. Unknown members and unsupported operations, including
dynamic/deferred/callable constructs, fail closed.

The plan retains object-initializer children, checked/lifted operators and conversions,
argument ordinal/ref-kind, local/ref identities, exact foreach calls, and CFG captures,
lexical regions, branch/return edges and finally transitions. CLR value-type default construction
is distinct from the optional-argument `TransactionResult` constructor: `Ok = new()` is not
collapsed into `EvmException(..., null)` and its empty description.

## Operation ownership audit

The admitted operation interfaces were checked against the pinned Roslyn 5.6 public API.
`Target`, operator/conversion metadata and recursively lowered children cover the following
groups; admitting a new operation kind still requires extending this audit.

| Operations | Additional ownership or semantic information |
| --- | --- |
| Invocation, object creation, field/property references, binary/unary/compound operators, conversions | Exact resolved member, constrained type, checked/lifted/ref/virtual flags and argument position/ref-kind; user-defined conditional operators fail closed. String concatenation is explicitly typed and admits only string/string operands; object/value formatting (including compound assignment) fails closed because Roslyn does not expose its implicit ToString call. Tuple binary operations and callable/dynamic operands are not admitted. |
| Arguments and coalescing | Both argument conversions and `ValueConversion` resolve user-defined methods and retain their conversion metadata. Nonidentity tuple conversions fail closed because nested conversions are not exposed by `CommonConversion`. |
| Collection expressions | `ConstructMethod` is resolved and encoded as the target. Constructor arguments fail closed. Arrays have no implicit user constructor/Add call. Nonempty collections are restricted to the pinned metadata `JournalCollection<T>` with its unique exact `Add(T)` target and typed element operations; other construction shapes and all spreads fail closed. Roslyn exposes no collection-expression `AddMethod`, and its collection-initializer symbol query returns no target for these elements. |
| Object/collection initializers and member initializers | Their child assignments, member references, conversions and implicit Add invocations are lowered and owned normally. |
| Deconstruction and foreach | Semantic-model deconstruction information records and resolves every nested method/conversion. Foreach also binds enumerator, MoveNext, Current, Dispose, element/current conversions and element type; async iteration fails closed. |
| Using declarations | Only synchronous `StackAccessTracker` disposal is admitted, with its resolved `IDisposable.Dispose` implementation and the CFG finally/leave transitions. Other resource types and using statements fail closed. |
| Constant, negated, declaration and discard patterns; is-pattern and switch expressions/arms | Input/narrowed/matched types, declared locals, null matching and exhaustiveness are retained. Recursive/list/relational/other patterns are not admitted. |
| Blocks, declarations/initializers, tuples, local/parameter/instance references, captures and branches | Local identities/scopes, tuple natural type, declaration flags, capture initialization, branch targets and CFG regions are retained; ignored declaration operands and writable ref locals fail closed. |
| Literal/default/discard/nameof, expression statements, returns/throws, conditional/access/null tests, parentheses and array construction/initializers | Typed constants, ordered children and their operation kinds carry the remaining semantics; these interfaces expose no additional hidden callable member. |

Regression mutations include a throwing `StageBProbe : List<int>` constructor reached through
`StageBProbe probe = [];`, hidden coalescing/collection/deconstruction conversions, custom
Deconstruct and Dispose implementations, spreads, tuple conversions and recursive patterns.
They must compile before exact ownership/unsupported-operation diagnostics are asserted.
String-formatting regressions include a throwing ToString override on a value type, either
operand position, boxed objects and compound concatenation; explicit user conversions to
string remain subject to exact callee ownership. Primitive numeric/Boolean/enum operations,
reference equality and sealed string equality introduce no overridable user-code dispatch.

## External obligations and remaining work

External premises explicitly include VM/processor/receipt header aliasing, `Author ?? Beneficiary`,
the `CodeInfo.IsEmpty` analyzer sentinel, normal callbacks with heap noninterference, and access
collection borrowing/snapshot-before-disposal. Referenced CLR types are normally initialized at
entry; selected readonly initializers describe value provenance, not concurrent type initialization.
Pool freshness requires the complete
`Dispose -> ResetAndReturn -> Clear -> pool` lifecycle, not just allocation syntax.

The lowering checkpoint by itself has no independent specification, source-refinement proof, or
serialized Stage-B schema/emission gate. No old Handoff, state-charge, pre-refund, settlement, or
completed-result oracle has yet been removed from the old Lean model.

Focused tests compile in-memory mutations against the real EVM closure. Initializer copy-back,
UInt256 operator, same-type fee arguments, and static transfer-signature mutations are checked
against executable terms, not only source hashes. Exact-diagnostic cases cover dynamic invocation,
deferred lambdas, unsupported loops, unknown method/field ownership, changed sentinel semantics,
and a changed transitive refund helper. Stage-A behavior and the stale generated artifacts remain
separate preservation checks. No additional production defect was confirmed.

## Prefix compiler checkpoint

`StageBPrefixCompiler.Compile` consumes only the operation/CFG plan and its structured member,
parameter, receiver, local, capture and value-layout bindings. It starts at the recorded successful
nonce continuation. Pure expression-bodied members have a source-derived single-block CFG envelope;
the compiler never executes the descriptive statement copies.

The bounded call closure ends at the resolved `Refund/12` invocation after its receiver and ordered
argument expressions. Argument evaluation order is distinct from parameter ordinal binding; readonly
locations, temporaries, ref/out locations and conditional-ref capture aliases remain distinct.
The implicit final false argument is preserved, and equivalent explicit `this`/false spellings are
admitted. Suspension retains the pending parent assignment and original after-call CFG as inert
continuation data, without a refund result. Access reporting, fees and finalization are not compiled
into the executable prefix. Their appearance before the boundary fails closed.
Refund receivers captured by newly introduced conditional argument evaluation currently fail closed;
this checkpoint admits direct implicit or explicit `this`, not general receiver-alias reconstruction.

Initialization and state-charge entries have distinct accepted-stage instruction kinds. Static field
reads refer to separate, completed pre-entry initializer provenance, not runtime helper invocations.
Local-call cycles, CFG cycles, finally transitions and unimplemented reachable operations fail closed.
The compiler records a callee-before-caller ordering and per-block/function conservative fuel bounds
for CFG dispatches and term visits with local calls expanded; these are not a Lean termination proof
or a bound on external implementation work.

## Executable prefix candidate

`StageBPrefixInterpreter.Run` now executes a bounded subset of the compiled CFG and local-call DAG
from the exact post-nonce entry to the pre-`Refund` suspension. The runtime distinguishes values,
readonly and writable locations, ref/out aliases, conditional-ref captures, value-type field copies,
static initializer cells, source argument order, parameter ordinals, CFG edges, returns, and fuel.
The suspension retains all twelve evaluated Refund operands, their passing modes, and stable location
identities. In particular, `tx.ValueRef`, `gasAvailable`, `opcodeGasPrice`, `floorGas`, and
`standardGas` cannot be collapsed into an unlabelled value oracle.

The supported input slice is ordinary standard-mainnet commit/no-restore processing with EIP-7708
transfer logs and warmup balance truncation disabled. State tracing is also disabled. It executes zero- and nonzero-value transfers,
existing and dead recipients, the EIP-8037 new-account state charge, spill, and state-charge OOG gas
clear. Initialization failure is excluded from the selected entry domain rather than modeled as a
production failure-order execution; that exclusion and other input-known unsupported routes reject
before issuing a request.
Code lookup, dead-account observation, balance writes, Commit, and tracer/metric callbacks use exact
typed request/reply records; there is no generic projection or external-call response oracle.
`CodeInfo.IsEmpty` is a reply field independent of code length.

Addresses are opaque canonical identities supplied by the input/tape, not proved 20-byte values or
an address-encoding model. Empty journal collections and empty byte buffers are represented up to an
observational quotient: backing-object identity is not retained. The supported prefix has no operation
that can observe those identities; reference equality over such quotiented values rejects. The runtime
therefore is not a general CLR heap or struct-alias model.

The transaction-initialization and state-charge atomic instructions call the two production kernel
source files linked into this project. The executable program retains scope, compiler source/reference
pins, external premises, stage theorem/source closures, and a whole-program integrity hash. Runtime
checks pin the two linked kernel stage closures and the production cap/new-account constants. The
integrity hash detects accidental in-memory mutation only; it is not a trusted admission boundary
because a caller can recompute it after changing a supported program field. Changing either linked
production kernel requires updating the independently visible closure gate and its tests.

This remains an execution candidate, not a source-refinement theorem. It has no independent Stage-B
specification, callback-exception model,
EIP-7708 log construction, warmup balance reads, or post-Refund execution. The checked-in Generated
triplet and public Stage-A extraction are unchanged and remain unaccepted for this newer route.

## Serialized program-data checkpoint

`--stage-b` publishes a separate `StageB/Generated` triplet; it never rewrites the stale Stage-A
`Generated` files. `PrefixProgram.ir.json` is the canonical, field-complete serialization of the
compiled `StageBPrefixProgram`. `PrefixProgram.lean` is typed program data over `StageB/Syntax.lean`,
not a hand-shaped result formula. It carries the complete functions, CFG blocks, nodes, calls,
bindings, captures, regions, source terms, continuation, source/reference pins, accepted-stage
closures, external premises, members, types, initializers, callee order, fuel, and exact pre-Refund
suspension. Raw Roslyn member, operator, conversion, and constant identities remain present alongside
exact primitive tags; unrecognized identities are tagged `unsupported` rather than interpreted by a
name suffix or declaring-type pattern.

The artifact boundary hard-pins the admitted prefix digest independently of the program's mutable
self-integrity field. JSON admission rejects unknown, duplicate, missing, null, noncanonical, and
integer-enum fields. The manifest binds the canonical IR, emitted Lean, and Syntax bytes with exact
paths and hashes. Promotion checks compare supplied IR, Lean, manifest, and Syntax bytes with a fresh
source-derived regeneration; publication atomically replaces IR and Lean before atomically replacing
the manifest last. Deterministic dual extraction, schema/adversarial checks, checked-artifact
regeneration, and direct Lean compilation are executable gates.

`StageB/Syntax.lean` remains the lossless emitted-program representation bound by the manifest.
Handwritten runtime carriers are isolated in `StageB/RuntimeSyntax.lean`, so extending executable
semantics cannot silently change the accepted serialized schema or its hash.

## Generic Lean prefix semantics checkpoint

`StageB/Semantics.lean` is a total, separately fuelled interpreter over the actual emitted functions,
blocks, edges, nodes, children, calls, bindings, captures, and Refund suspension. It does not replace
the emitted graph with transfer phases. C#-step fuel follows the executable candidate's block/node
ticks, while a separate micro-fuel bound makes the Lean evaluator total. Static initializers are lazy,
frames and cells receive fresh internal identities, ref aliases retain field paths and permissions,
and literal-vector observations project stable provenance labels rather than exposing allocation-order identifiers.
Initialization and state charging call the existing generated kernel definitions. External effects use
typed request/reply tape entries, including the fallback Commit before leaving the selected prefix;
a wrong request or reply type consumes and records nothing.

The public `StageB.Runtime.commonDomain` predicate defines a common C#/Lean execution domain that is
deliberately narrower than the C# candidate's complete input carrier. It requires EIP-8037 enabled,
EIP-7708 and authorization processing disabled, no warmup,
state, action, code and log tracing disabled while access tracing may have either value, no code override,
no forced fast-path disable, commit without restore, a present
recipient, the pinned 16,777,216 execution cap and 183,600 new-account state cost, representable
`ulong`/`long`/`UInt256` inputs without aggregate `ulong` wrap, a transaction gas limit no greater than
`Int64.MaxValue`, standard intrinsic execution gas no greater than the pinned cap, successful
initialization, and a typed normal-return tape. The last two range guards match the normally validated
standard-mainnet route: they are not derived from the replay carrier's `commit` flag, and standalone
`SkipValidation` execution is outside this selected domain. They let
`commonDomain_initialization_refinementValid` discharge the independent transaction-initialization
kernel's complete fixed-width refinement predicate. Addresses remain opaque canonical identities. Nonempty or delegated recipient code
is modeled only through the exact CodeLookup then Commit request sequence and an outside-domain result.
Post-Refund execution remains outside the frozen prefix/replay model, whose suspension is terminal; a
separate residual leaf below models the block-37 access guard and argument evaluation up to the first
call in its selected block-38/39 successor, stopping before applying that call.

`StageB/Vectors.lean` contains fail-closed `native_decide` propositions at global program fuel 853.
The compiler charges every direct static-field access for its cold initializer and uses the root
function bound directly, rather than adding a detached whole-program initializer surcharge. The frozen
`CalculateAvailableGas` function therefore has the conservative generated bound 106; its actual cold
success route consumes 80 ticks, while the former 79-tick metadata exhausted at the terminal block.
They cover zero, nonzero, self, existing and dead recipients; reservoir consumption and spill; exact
state-charge and OOG boundaries; OOG enum value 4 and gas clearing; complete ordered Refund values,
passing modes and resolved provenance; exact remaining tape; Commit fallback; request/reply mismatch;
C# fuel admission and exhaustion; micro-fuel exhaustion; and pinned-constant rejection. Runtime values
are compared through a length-delimited structural encoding that retains every constructor, type,
identity, field name, field order, byte, and nested value up to a fixed depth exceeding the admitted
program's layouts. `Verify-StageB.ps1` checks artifact freshness, the pinned ordinary-Refund adapter
manifest and source closure, the standard-mainnet Refund-dispatch artifact, focused warning-as-error C#
tests with a fail-closed minimum of 556 discovered Stage-B tests, the Stage-B Lean target, direct
warning-as-error typechecks, and placeholder absence without depending on the known-stale Stage-A
reference target.

These literal vectors are executable Lean evidence cross-checked by separate C# literal tests for the
stated common domain. They are not a Lean refinement theorem or general CLR semantics. The separate
shared replay checkpoint below compares actual executions; no theorem equates the C# and Lean
interpreters or establishes production-source refinement for the whole transaction processor.

## Shared replay checkpoint

`StageBReplay.cs` and `StageB/Replay.lean` independently decode the same UTF-8 NDJSON records. The C#
test serializes each input once, decodes that exact record for its own execution, and sends the same
bytes plus one LF delimiter to the native `stage-b-replay` Lean executable. Lean removes only that LF
before decoding. Blank records fail; EOF terminates. The executable computes observations through
`runGeneratedObserved`, not `#eval` output or expected results embedded in a generated Lean file.

The version-1 wire schema uses fixed-arity arrays. Integers are canonical decimal strings, bytes are
lowercase hexadecimal, booleans remain JSON booleans, and an absent optional string is JSON null.
Objects, missing/extra fields, unknown tags, numeric JSON integers, noncanonical decimal strings,
out-of-range values and invalid string scalars are rejected. A record is bounded to 1,048,576 UTF-8
bytes, the tape to 4,096 exchanges, and micro-fuel to 1,000,000. JSON whitespace and equivalent string
escaping do not change the decoded record. Writers are deterministic; output comparison is complete
structural JSON equality, not byte equality of JSON escaping.

```text
input = ["stage-b-replay-v2", id, prefixIntegrity, csharpFuel, microFuel, rawInput, tape]
rawInput = [transaction, spec, tracer, intrinsic, restore, commit, deleteCallerAccount,
            warmup, opcodeGasPrice, premiumPerGas, senderReservedGasPayment, blobBaseFee,
            isCodeOverridable, forceSimpleTransferDisabled, executionGasLimitCap, newAccountStateCost]
transaction = [sender, recipient, value, dataHex, gasLimit, hasAuthorizationList]
spec = [eip8037Enabled, eip7708Enabled]
tracer = [state, actions, code, logs, access]
intrinsic = [standardGas, floorGas]
gas = [value, stateReservoir, stateGasUsed, stateGasSpill, stateGasSpillRefunded]
exchange = [request, reply]
output = ["stage-b-replay-v2", id, prefixIntegrity, outcome, rejection, remainingCSharpFuel,
          requests, remainingTape, returnValue, refundOperands]
operand = [ordinal, passingMode, typedValue, locationOrNull]
location = [canonicalRoot, fields, effectiveReadOnly, fullProvenance]
```

The access flag is independent of action tracing. Version-1 records, four-field tracer arrays, and
otherwise malformed records are rejected by both decoders.

Request tags are `code`, `empty-calls`, `dead`, `subtract`, `add`, and `commit`; reply tags are `code`,
`bool`, and `unit`. Request and reply are decoded independently so a wrong reply kind or mismatched
request survives intact until the interpreter rejects it. Such rejection consumes and records
nothing. Input/fuel admission and expected tape failures have explicit typed rejection categories;
the comparator does not classify English diagnostics. Unexpected interpreter rejection, CLR fault,
micro-fuel exhaustion, duplicate result identities, missing/extra results, or malformed output fails
the harness. The C# replay adapter enforces the same narrower `commonDomain` as Lean before entering
the wider C# candidate. Valid C# execution fuel is `1..853`; micro-fuel is a separate evaluator budget.

Every terminal observation retains remaining logical C# fuel, all ordered requests, and the exact
unconsumed exchange suffix. Suspension additionally carries exactly ordinals `0..11`, all argument
modes and complete recursive typed values. Struct fields are compared as name/value maps in sorted
name order; duplicate Lean field names fail encoding. Location roots are renumbered by first
occurrence across those twelve operands, using resolved C# object identity or Lean `CellId`, while
retaining the full field path, effective readonly permission and exact source provenance. Thus the
comparison preserves the observable alias partition rather than collapsing cells with equal source
names. It does not compare unreachable cells or the entire internal frame/capture heap.

The focused corpus covers zero, existing, dead and self transfers; reservoir/spill and exact/OOG
boundaries; distinct gas fields and large UInt256 values; Unicode identities and nonempty data;
code/delegation Commit fallback; exact unused suffixes; missing/mismatched tape; fuel neighbors at
the suspension; and each common-domain exclusion. Schema negatives exercise both independent
decoders, and recursive scalar/array mutations ensure that no part of the resulting observation is
ignored. The existing independent literal vectors remain a separate check.

This establishes finite replay agreement only. Source-linked interpreter extraction/simulation,
fuel adequacy, Roslyn lowering correctness, the full live cell/frame representation relation,
production reachability and adapters, and post-Refund/receipt composition remain open.

## Source-attached control leaf

`StageBControlKernel.Tick`, `SelectEdge`, `FinishBlock`, `SelectLocalReturn` and
`ShouldReadTransparent` are allocation-free value functions used by
the candidate C# interpreter. Tick tests zero before decrementing; its exhausted result retains zero.
SelectEdge checks boolean validity before selecting an optional conditional successor, falls back
when that successor is absent, and distinguishes missing, negative-return, invalid-negative and
nonnegative jump edges. `FinishBlock` checks direct Return and Suspend before invoking `SelectEdge`.
Return preserves the evaluated value; Suspend rejects a block which did not already suspend.
Its generic cursor carries the current block ordinal and an opaque value. A jump changes only the
ordinal to the selected destination; every other result preserves the whole cursor. The caller
consumes a successful cursor in one assignment. Its Outside check remains before operation and
branch evaluation. Suspension still bypasses the helper. Operation-level Return is excluded by the
prefix compiler and rejected recursively by both interpreter admission paths, so every admitted
normal return reaches the helper as a CFG Return exit rather than through a Return signal.
`SelectLocalReturn` chooses the ordinary returned value, a constructor receiver value, or the
unchanged constructor receiver location. The candidate invokes it after the callee's `Execute`
returns normally. Only the receiver-value outcome reads the receiver; both location modes preserve
the receiver operand without reading it. Root result publication and static-field initialization
remain separate caller paths, covered by the existing Machine token and call-target admission pins
rather than this extracted leaf.

`--stage-b-control` resolves the actual package's Release/net10.0/C#14 Compile and ReferencePath
inputs through MSBuild. Roslyn binds those complete sources against the real metadata references;
there are no invented stubs. The separate `StageB/Control/Generated` triplet contains a small semantic
IR, theorem-free Lean functions emitted from bound operations, and canonical source/reference/build
input hashes. The generated prefix data and accepted `StageB/Syntax.lean` are unchanged.
This is a reconstruction of the actual Compile/ReferencePath front-end inputs, not emitted IL or a
reproduction of every analyzer run. The package currently invokes no source-generator trigger; the
kernel and result declarations are non-partial and exactly admitted. SDK analyzers/generators and
their noninterference with the admitted declarations remain in the compiler trust boundary.

The version-2 control bundle admits five exact signatures, initialized immutable locals, pure early-return
conditionals, primitive comparisons/boolean operations, checked result layouts, and unchecked Int64
subtraction with explicit modular wrapping. Unary negation is restricted to nonnegative Int32
literals; arbitrary unchecked negation is rejected. Constructor arguments must remain in parameter
order. The only admitted call is the exact `SelectEdge` root; its argument order is preserved in the
emitted term. Generic cursor/result records have exact positional constructors and automatic getters.
The unconstrained carried type is opaque: it can be retained or projected, but default construction,
boxing, inspection and method calls are rejected. Mutation, unknown operators and unsupported
control flow are rejected. The source
hashes identify the inputs; they are not the semantics or the correctness theorem.

Adapter admission checks exact caller arguments, decision order, diagnostics, result handling and
writeback; semantic bindings reject nested/shadow result or enum types. It checks auto-property and
positional-record representations, the actual Int64 `_fuel` field, the initial-domain guard and
constructor, the two Tick sites, and the direct symbol-bound fuel writes (constructor and Tick),
rejecting ref aliases, deconstruction and direct mutations elsewhere. Machine must be a single,
non-partial, attribute-free private sealed class, with an exact attribute-free Int64 fuel field;
attributes on sibling fields are also rejected. This excludes explicit-layout/FieldOffset storage
overlap, sequential-layout/packing changes, partial-declaration attribute hiding, and aliased
attribute names without trying to infer which individual attributes might be harmless. Compile-first
regressions include an overlapping `_aliasFuel` write which contains no write to the `_fuel` symbol.
It also rejects visible
reflection, dynamic, Unsafe and UnsafeAccessor calls in the Machine source. C# TypedReference
intrinsics (`__makeref`, `__refvalue`, `__reftype`) and the `__arglist` expression/declaration family
are rejected in both the Machine and control-kernel source, before lowering or direct-write checks.
Compile-first negatives cover the exact alias/write bypass and each related intrinsic family. This is bounded
source-shape/binding evidence, not a universal heap-write proof or a proof of arbitrary callees.
A fixed semantic target/cardinality roster traverses Machine's bound operation subtrees, including
implicit conversion operations and argument/compound-assignment/coalescing conversion metadata.
It binds same-compilation invocation, construction, property-accessor, method-reference and
user-defined operator/conversion target slots; syntax-span/role/target deduplication makes repeated
Roslyn syntax views stable. These are admitted target-slot counts, not dynamic call counts. Newly
introduced source helpers (including an enclosing reflection helper) cannot silently become opaque
premises. The roster is not a proof of callee bodies, call argument values or full statement order.
Metadata-only calls and compiler-generated protocols are not claimed verified by this roster.
A separate SHA-256 admission pin covers the entire single Machine declaration's ordered token
kind/length/text sequence, excluding trivia. Every token change therefore requires explicit
rereview, even for events, disposal, iteration, interpolation handlers or implicit receiver capture
outside the roster's modeled call surfaces. This is conservative change detection, not semantic
extraction, callback noninterference or a substitute for the generated kernel proofs; unchanged
tokens also do not prove unchanged bindings in dependencies. Semantic bindings and the call roster
remain separately checked, and compiler/opaque-transitive-operation premises remain explicit.
Direct fuel writes/ref aliases are scanned over the single enclosing interpreter declaration;
bound increment/decrement targets are rejected independently of parentheses, member access and
checked/unchecked wrappers. Explicit Machine-instance escapes and lambda captures of the fuel field
are rejected. Existing
noninterference from opaque/transitive CLR operations remains a premise. It is not a proof of the
rest of those callers or the full interpreter.
The local-return suffix is separately admitted with callee execution before selection and lazy
receiver-value projection. Its operand-mode and return-kind enum representations, node-mode
projection and bound helper identity are checked. A same-shaped shadow return type with an
implicit conversion is rejected before it can change the result test.
The transparent-wrapper suffix separately admits arity validation before child evaluation, exactly
one child at index zero, both Conversion branches before selection, the exact Argument or
DeclarationExpression preservation predicate, node-mode projection, the bound helper call, a lazy
single read on its true branch, and identity return on its false branch. Evaluate's preceding Tick
and exact dispatch into this suffix are bound independently.
The interpreter's exact pure supported-operation predicate and the complete operation-root,
branch-value-root and recursive-child validation traversal are also admitted. Return is absent from
that predicate, matching
the compiler's existing `RequireOperation` whitelist; compile-valid attempts to re-admit it or omit
either root or child traversal fail closed. The exact operation enum and Program, Function, Block and
Node positional-record shapes prevent value aliasing and behavioral getters from separating
validation from execution. Their arrays remain mutable, so this local claim assumes a frozen,
single-threaded program graph with no concurrent mutation between validation and execution.
The stored `Frame.Function` projection and positional `StageBCfgPoint.Block` representation used by
`Execute` are pinned separately; behavioral getters or adjusted entry ordinals fail extraction.

`Control/Specification.lean` independently defines a natural-number tick, an optional-edge decision,
and a block-finishing transition over an arbitrary carried type. `Control/Refinement.lean` proves `extracted_tick_correct` for every
represented nonnegative Int64 fuel value and `extracted_selectEdge_correct` for all represented
condition strings, boolean facts, optional edges, and destinations. The second theorem explicitly
relates flat C# arguments to optional specification values and encodes every result tag/destination.
Int32 destinations are a subset of its more general mathematical domain. String representation
requires nonnull scalar strings; the fixed program uses the three literal condition labels. These
proofs are kernel checked and do not use native evaluation, placeholders, or assumed result equality.
`extracted_finishBlock_correct` covers every result kind and complete cursor, including direct
Return/Suspend precedence and identity-preserving carried values. `generatedFinishBlock_jump_step`
connects the generated helper's jump result to the existing `Runtime.step` finish-block transition
with an arbitrary remaining work list. Its exact Machine equality preserves operands, frames, cells,
statics, tape, request history, returned value, allocation counters and C# fuel; only the head task
becomes entry to the returned block ordinal. The relation binds the frame/function identity, current
block ordinal and Boolean projection of the actual carried value. It is a local post-evaluation
theorem; it does not model the recursive CLR stack or return/exception unwinding.
`extracted_selectLocalReturn_correct` relates every pair of construction/value-mode flags to the
independent three-case local return decision. It does not assume that a receiver location is
readable, nor does it prove the recursive caller stack corresponds to the Lean resume tasks.
`extracted_shouldReadTransparent_correct` relates all four preservation/value-mode pairs to an
independent selector. `generatedTransparent_entry_step` composes the actual generated Tick result
with the exact one-child eval/apply schedule, while `generatedTransparent_completion_step` uses the
generated selector directly to pop the child result and either retain the identical operand or push
one readable value. Both give exact whole-Machine successors and preserve the caller operand tail.
`setLast_success_step` then models the enclosing C# `last = Evaluate(...).Read()` boundary: it reads
and pops the returned operand and updates the selected frame's carried value. Its conclusion uses the
runtime's exact `replaceFrame` mapping, so malformed duplicate frame IDs are all replaced with the
updated first matching frame rather than hidden behind an unproved uniqueness assumption.
`generatedTransparent_setLast_two_step` composes transparent completion with this update, derives the
generated selector through the existing refinement theorem, and requires unconditional outer
readability because C# `Execute` always calls `Read` even when the wrapper itself preserves a
location operand. This is a success-only local equation. Exactly 38 eligible ExpressionStatement
nodes are operation roots immediately followed by `setLast` in the emitted schedule; the other 138
covered wrappers are nested and require their enclosing parent compositions before this bridge.
`enterBlock_success_step` connects the generated Tick result to the exact Lean block-entry schedule:
operations remain in list order and each is followed by `setLast`, the optional branch follows every
operation and is followed by `setLast`, and `finishBlock` precedes the arbitrary existing work tail.
The equation preserves the complete Machine outside fuel, work, and the runtime's exact frame
mapping. A duplicate-ID example makes the otherwise non-obvious mapping explicit: block entry
normalizes every matching entry to the first selected frame even though carried is assigned to
itself. Separate equations cover zero fuel, one-tick missing-block rejection, and Outside after one
tick but before frame replacement or any operation/branch evaluation. Function/frame lookup and
association are premises: C# receives a concrete Frame and constructs a unique-key block dictionary
before its loop, whereas the generic Lean runtime performs first-match list lookups after Tick.
`generated_program_dictionaryShape` now discharges the fixed emitted artifact's structural side:
ordinary kernel reduction proves 26 distinct function symbols, 149 blocks with ordinals unique within
each function, the selected program entry's presence, and every function entry block's presence.
Generic key-uniqueness lemmas give exact function and signed-ordinal block lookup equations, including
negative-ordinal rejection; duplicate/missing-entry examples fail while case-distinct symbols and
equal ordinals in different functions remain valid. This predicate is deliberately separate from
`programValid`, which does not imply it. CLR Dictionary/string/integer correspondence, frozen array
ownership and runtime frame/function association remain open adapter obligations;
these equations do not claim matching malformed-program failure order.
`generatedFinishBlock_return_resume_step` proves that an actual generated Return result skips a
resume-free task prefix and selects the nearest resume without changing other Machine fields;
`generatedFinishBlock_return_root_step` proves the corresponding exact root return observation for
a resume-free tail. Both derive the original block ordinal and carried value from the generated
cursor rather than accepting them as premises. `generatedLocalReturn_resume_step` connects the
generated three-way decision to the exact local resume transition, and
`generatedFinishBlock_return_local_two_step` composes the finish and resume steps. Constructor
receiver readability is required only for value mode; location and readonly-location modes preserve
the operand without reading it, matching the candidate C# evaluation order. These are operational
equations requiring the exact frame lookup, an Inside block, a resume-free skipped prefix or root
tail, and conditional receiver readability. They do not establish that every recursive CLR caller has
the corresponding resume-free Lean task prefix or that retained frames and cells represent the CLR
stack after unwinding.
`generatedFinishBlock_return_local_setLast_three_step` additionally composes the caller's outer
`setLast`: the local resume pushes exactly one selected operand, `setLast` consumes it, restores the
original operand and work tails, and updates the selected caller frame's carried value. Constructor
location results are read at this final step, while ordinary calls still require no receiver read. The
theorem retains the runtime's all-matching replacement behavior for duplicate frame IDs; it does not
prove that the starting work stack is reachable from C# call entry, that the callee ran correctly, or
that successful return removes a CLR-equivalent callee frame.
`StageB/Admission.lean` adds the complementary static-program fact. `NodeReturnFree` is recursive,
`programValid_returnFree` derives it from the actual generic admission predicate, and
`generated_program_returnFree` kernel-checks the complete emitted program with ordinary `decide`.
The Return operation enum remains serializable, but admission and a manually forced apply step both
reject it. CFG Return exits remain distinct and are the return mechanism covered above.
The generic handwritten `createFrame` model now follows the candidate's local binding branches:
immediate operands for writable `ref`/`out` parameters reject, all five reference kinds retain an
existing location without reading it, the three readonly-reference kinds allocate a readonly
temporary for an immediate operand with mode-selected provenance, and ordinary parameters copy one
read value. Descriptor-free calls with parameters check arity before reading and bind positional
value copies regardless of declared ref kind; a zero-parameter fallback ignores surplus operands.
Seven operational equations fix these cases. Generic allocation theorems now give every successful
binding list's exact added-cell prefix and unchanged old-cell suffix, total, consecutive reverse ID
sequence, owner and final counter. Under the explicit premise that all old cell IDs precede
`nextCell`, prior successful cell
lookups and operand reads survive the extension, including already-terminating alias chains. A
universal mixed theorem proves the complete post-state and symbol/cell lookups for an existing
reference location followed by a readonly immediate and an ordinary copy; descriptor-free
corollaries prove one allocation per copied operand and no cells for the zero-parameter fallback.
This is not arbitrary-list parameter-content refinement: exact contents are proved only for that
ordered mixed family, constructor use starts after `localCallPreparation`, and the results do not
relate CLR locations/cells to Lean locations/cells, cover malformed explicit descriptor/index failure
ordering, or compose a complete recursive call simulation.

Successful all-by-value explicit descriptors now have a separate duplicate-safe layout theorem. Each
descriptor allocates one non-alias cell in descriptor order, the prepended raw-cell list records that
order in reverse, and final frame lookup selects the latest binding when symbols repeat. With unique
selected symbols, descriptor position `i` maps exactly to the incoming `nextCell + i`; no freshness or
initial-read premise is needed because copied payload equality is not claimed. Ordinary-kernel frozen
facts pin the two actual local constructor plans: `TransactionResult` selects ordinals `0,2,1` and
includes the implicit `DefaultValue` argument, while `TransactionSubstate` selects all eight parameters
in ordinal order. All selected parameters are by value with unique exact symbols. The respective
three- and eight-cell layouts remain conditional on successful binding and do not prove child
evaluation or dynamic reachability.

Constructor preparation is now composed with successful argument binding without collapsing their
different owners. An exact equation allocates the writable receiver at the original `nextCell` under
the caller; the conditional composition keeps that receiver immediately before the unchanged old-cell
suffix, proves it remains findable, and places the callee-owned parameter prefix at consecutive IDs
beginning at
`nextCell + 1`, with exact cell-list, counter, callee-frame and final ID-bound facts. The theorem
assumes successful binding and the old-cell ID bound. It does not prove target selection, dynamic
reachability, C#/Lean ownership or default-value correspondence, constructor-body execution, or the
four frozen object-creation sites routed through default/external targets.

The frozen `TransactionResult` path now closes the actual `GasLimitBelowIntrinsicGas` initializer
block entry through exact constructor entry. The first microstep performs the generated block-entry
tick and preserves the runtime's all-matching `replaceFrame` normalization. The following 15
microsteps evaluate the emitted object-creation root over eight more ticks: the error field becomes
`ErrorType.GasLimitBelowIntrinsicGas`, the complete string literal becomes the textual reference
`"intrinsic gas too low"`, and the implicit zero conversion is retagged as `EvmExceptionType.None`.
The seventeenth microstep composes that result with the exact `0,2,1` binding theorem. The resulting
Machine has the unchanged operand tail, tape, requests, statics and returned value; the exact
caller-owned default receiver, including null `ErrorDescription`; three callee-owned non-alias
parameter cells; exact frame bindings and counters; and `enterBlock` followed by the constructor
resume, under explicit frame-presence, function-association, fresh-frame-ID and fixed-width fuel
premises. A separate old-cell-bound theorem excludes collisions between the four new cell IDs and
pre-existing cells. Starting the initializer with eight ticks, an exact 12-step equation preserves
the whole heap and allocation counters after frame normalization before the thirteenth step reports
fuel exhaustion, so receiver or parameter allocation cannot occur.

The private C# probe now invokes the actual initializer `Execute` boundary rather than supplying
prepared operands or starting directly at its branch node. It observes nine ticks per completed call
and 18 across its two calls, including remaining fuel 9 then 0 at the two constructor entries; both
entries have the exact value/type/default-field mapping and exact selected callee function. It also
checks unchanged caller cells and captures, null aliases, distinct receiver/frame/parameter objects
across two calls using physical reference equality, and no constructor entry or allocation with
either seven or eight ticks. Separate boundaries preserve an empty or colon-containing canonical
string payload and reject mismatched encodings. Compile-valid mutations remove each new value/default
branch and must reach the fail-closed control admission gate.
The C# `Reference` value and Lean `.reference` value are a textual quotient for this copied literal;
they do not assert physical CLR string identity.

The exact generated value-mode static-field node in function 9, block 3, now has two lower-level
operational equations. With a cold cache, an associated caller frame, fresh frame IDs and the
fixed-width fuel bound, 19 microsteps and ten ticks enter the exact function-8 initializer and compose
through its object creation to the constructor entry above. The static cache is still unchanged there,
and the work tail contains the exact nested static-field resume. With an existing readable cache cell,
two microsteps and one tick return its immediate value without allocating a frame or cell. The generic
cached value-mode branch was also corrected to reject a failed cell read, matching the executable
candidate; separate success and failure equations pin that behavior.

The frozen canonical Machine now also has an exact end-to-end Lean witness. Over arbitrary `Input`
and residual fuel, 43 microsteps and 18 ticks continue from constructor entry through its three ordered
assignments, terminal block, constructor receiver projection, initializer `setLast`/return and static
publication. Composed with the cold prefix, the complete selected-node path is 62 microsteps and 28
ticks when `fuel + 28` fits the production signed-`long` domain. The explicit final state contains the completed value on the operand stack, exactly one static
binding and cache cell, the three unchanged parameter cells, the updated receiver, the retained
constructor and initializer frames, `returned = none`, and the exact cell/frame counters. Companion
equations expose the assigned but unpublished state after 56 microsteps with 27 ticks and fuel
exhaustion on microstep 57. A separate universal one-step theorem states the handwritten value-mode
publication rule for any Machine.

These completion equations intentionally use an empty surrounding work/operand/frame/cell/static/tape
state with fixed initial counters. They do not prove arbitrary old-suffix preservation, live-location
noninterference or caller-frame reachability. Numeric Lean IDs can collide with malformed old cells or
dangling aliases in ways C# object references cannot; a future well-formed reachable-heap relation must
exclude those states. The two retained Lean frames and four constructor cells are internal model state,
not a claim that completed recursive C# frames remain live.

The corresponding executable probe exercises the real selected node. Cold completion consumes 28
ticks, publishes exactly one static cell only after the initializer returns normally, and a following
cached read consumes one tick and returns the identical value object. With 27 ticks, execution reaches
the constructor terminal-block tick after all three receiver assignments, exhausts fuel, and publishes
nothing. The probe distinguishes the constructor's returned description string from the initializer's
completed struct, checks parameter-cell stability and the exact 28-tick success boundary, and retains
the caller receiver, marker value and null alias, capture, requests and cache state at their applicable
boundaries. Compile-valid mutations additionally reject premature publication, dictionary overwrite,
returning the constructor's carried value instead of its receiver, and skipping the terminal-block tick.

This node is deliberately a lower-level theorem: the currently admitted public-input predicate rejects
`gasLimit < intrinsicGas` before this failure branch can be reached. It therefore does not establish
outer `CalculateAvailableGas` reachability. Generic duplicate publication still differs from C#
`Dictionary.Add`, and the Lean evaluator can schedule children for an arbitrary static node whereas the
C# member path skips them; the frozen selected node is childless and the cold theorem assumes absence
of its key. The candidate's per-Machine cache is not CLR whole-type initialization: sibling initializer
order, `beforefieldinit` freedom, locking, reentrancy, failure poisoning and value-type copy semantics
remain outside the model. Physical heap correspondence, C#/Lean constructor execution/unwind
refinement and outer-call composition remain open.

This remains a source-pinned generated-node theorem plus matched executable and handwritten Lean
semantics, not an extracted C#/CLR heap-refinement theorem: `Machine.Call`, allocation and these value
helpers are not yet extracted into Lean. A general reachable-heap/value relation, non-local
object-creation routes, and complete interpreter/pipeline refinement remain open.
None of these model limitations is a production Nethermind defect.

The selected `TransactionResult.Ok` node in function 9, block 2, now has a separate
whole-Machine checkpoint over arbitrary surrounding state. Its function-6 initializer uses the
childless CLR-default target, so the completed value has zero error and exception enums and a null
description; it never enters the three-argument constructor. Cold completion takes eight microsteps
and three ticks, retains the old frame and cell suffixes, tape and request history, and adds one
initializer frame and one cache cell. A cached read takes two microsteps and one tick, preserves all
allocation counters and returns the readable cached value. The two-tick prefix reaches default-value
evaluation with zero fuel before publication. The cold equation requires the new frame ID to be absent
from the old frames. Separate successful cell-lookup and operand-read preservation requires the new
cache-cell ID to be absent from the old cells; it does not preserve failed reads or dangling aliases.
The equations use natural-number fuel. Correspondence to C#'s signed-long fuel additionally requires
`residual + 3 <= Int64.MaxValue` for cold completion and `residual + 1 <= Int64.MaxValue` for a cached
read. These selected-node equations now compose with the generated function-9 success continuation,
but `commonDomain` alone does not derive the caller heap, binding, access and freshness premises needed
to reach them from the enclosing setup.
The real interpreter probe checks zero through four
ticks, null versus empty description, normal-return publication, cache value identity, and preservation
of an unrelated static, caller receiver/cell/capture, request history and unconsumed tape. Compile-valid
mutations of default type, error enum, description and cached-value selection fail source admission.

The initialization boundary is now narrower than that earlier gap. The strengthened `commonDomain`
implies the independent initialization model's nine fixed-width validity conditions and the generated
kernel's successful outcome; a separate theorem equates all five generated gas fields with the natural
model. For generated function 5, an exact arbitrary-context equation covers block 0 through block-1
entry in two microsteps/one tick, and a second covers block 4, block 5 and the local resume in eight
microsteps/three ticks. A third exact equation composes block 1's generated initialization, assignment,
success-pattern and `whenFalse` transition in 45 microsteps/21 ticks, ending at block-3 entry with the
result binding/cell intact and the selected frame carrying `false`. Block 3 is exact in 76 microsteps/34
ticks, including both captures, object allocation, five field writes, out-alias copyback and the jump to
block 4. `generated_tryCreateAvailableFromIntrinsic_exact` composes the complete emitted success body as
`2 + 45 + 76 + 8 = 131` microsteps and `1 + 21 + 34 + 3 = 59` ticks. C# fuel correspondence additionally
requires `residual + 59 <= Int64.MaxValue`. A real-interpreter probe runs the enclosing generated
`CalculateAvailableGas` function and production initialization adapter, observes cold/cached success at
80/78 ticks and exhaustion at 79/77, and checks out-cell copyback plus cache/value identity. Argument
evaluation and binding are now exact from function-9 entry to the actual function-5 callee entry in 25
microsteps/13 ticks. The proof derives the five allocated cells, read-only temporary and preserved output
alias, including duplicate caller-frame normalization. The body theorem now carries the caller's exact
output cell and location through the full 131-microstep/59-tick path, including arbitrary alias chains,
provided exact successful-read and successful-write equations hold; the canonical unaliased theorem is a
thin specialization of this shared proof. A separate exact bridge composes the function-9 prefix and
body as `25 + 131 = 156` microsteps and `13 + 59 = 72` ticks. Its explicit premises identify the newest
caller output cell and successor counter, the emitted output binding, older-cell and frame freshness,
and successful reads and writes through the actual alias chain. The endpoint retains the helper frame
carrying `true` with both captures, places the object and result before the five bound cells and written
caller heap, leaves the Boolean result above the operand tail with the function-9 block-1 continuation
pending, and returns the exact residual fuel. C# signed-long correspondence additionally requires
`residual + 72 <= Int64.MaxValue`. The generated success continuation is separately exact: it consumes
22 microsteps/eight ticks for cold `TransactionResult.Ok` initialization or 16/six for a readable cached
canonical `Ok`. Composition yields conditional function-9 entry-to-return paths of 178/80 and 172/78.
The result is stated through `scheduleReturn`; companion equations distinguish a resume-free terminal
result, an enclosing resume retained with `returned = some Ok`, and the following unticked local-resume
step. Signed-`long` correspondence for the full paths requires `residual + 80 <= Int64.MaxValue` or
`residual + 78 <= Int64.MaxValue`. The executable probe's 79/77 one-short boundaries are not yet Lean
corollaries. The sole generated caller in `Execute` block 29 is now exact from its actual invocation
`.eval` task. The proof allocates the caller's `gasAvailable` local first, then the function-9 parameter
cells in signature order, giving the inner theorem the exact permutation `[n+1,n+3,n+2,n+4]`; the output
parameter aliases the caller local at `n`. The invocation reaches function 9 in 22 microsteps/11 ticks.
Composition with the accepted full function-9 theorem and the following unticked caller-local resume gives
201 microsteps/91 ticks cold and 195/89 cached. Its endpoint has the original work tail, `returned = none`,
canonical `Ok` above the original operand tail, unchanged requests/tape, and the modeled gas value visible
through the caller local. The premises make caller reads, output-local absence, prior frame/cell freshness
and post-body `Ok` readiness explicit; duplicate caller IDs retain `replaceFrame`'s all-matching
normalization. Signed-`long` correspondence requires `residual + 91 <= Int64.MaxValue` cold or
`residual + 89 <= Int64.MaxValue` cached. There is no 90/88 one-short Lean corollary.

The enclosing block-29 success condition now has a separate exact composition theorem. The selected
implicit conversion executes generated function 4 (`op_Implicit`) and function 3
(`TransactionExecuted`), including both local resumes, in 26 microsteps/ten ticks from its `.apply`
task with canonical `Ok`. The complete condition evaluates the result lvalue and call, assigns `Ok`
to `result`, converts it to `true`, updates the caller's carried value and finishes the conditional
block. `generated_execute_block29_condition_success_exact` takes 234 microsteps/104 ticks cold or
228/102 cached. `generated_execute_block29_success_exact` includes the actual block-29 entry tick and
frame normalization, taking 235/105 or 229/103. Both stop with `enterBlock` for block 31 above the
original work tail, before block 31 executes.

The exact final Machine restores the original operand tail, has `returned = none`, preserves requests
and tape, and has the specified residual fuel. The caller holds canonical `Ok` in `result`, the modeled
gas value in `gasAvailable`, and carried value `true`; the retained conversion and property frames also
carry `true`. With starting cell counter `n`, lazy `result` allocation uses `n`, gas uses `n+1`, and the
function-9 parameters use `n+2` through `n+5`, with the output parameter aliasing `n+1`. The final cell
counter is `n+15` cold or `n+14` cached; from starting frame counter `f`, the final frame counter is
`f+5` or `f+4`. Cold initialization adds the canonical `Ok` static cell, while the cached path retains
its readable canonical value. The explicit premises include `commonDomain`, a present Execute frame,
receiver and argument reads, absent `result` and `gasAvailable` locals, prior frame/cell freshness and
post-body `Ok` readiness. Signed-long fuel bounds are `residual + 104`/`+102` for the condition theorem
and `residual + 105`/`+103` for the entry wrapper, each at most `Int64.MaxValue`.

The following recipient branch is now exact. `generated_execute_block31_condition_exact` evaluates the
generated `simpleTransferRecipient is not null` pattern in 11 microsteps/four ticks. The actual-entry wrapper
`generated_execute_block31_exact` takes 12/five and schedules block 32 for a typed address or block 33 for
null. Pattern wrappers consume no hidden tick. The independent null theorem stops with block 33 scheduled;
the outside-domain result would require entering that block once more.

`generated_execute_block29_to_block32_exact` composes the accepted block-29 entry theorem with the non-null
block-31 branch. It consumes 247 microsteps/110 ticks cold or 241/108 cached and stops before block 32
executes. Besides the inherited block-29 premises, it requires the original recipient binding and a
successful typed-address read. The proof shows that the 15/14 fresh prefix cells cannot shadow any prior
successful lookup, preserves arbitrary alias chains, proves the result/gas symbol filters retain the same
recipient binding, and reaches the all-matching normalized caller frame. The endpoint has block 32 above the
original work tail, the original operand tail and residual fuel, and the already accepted block-29 heap,
requests, response tape and frame state. It has not evaluated the `ExecuteSimpleTransfer` receiver or
arguments and has not reached that call's suspension.

Generated `Execute` block 32 is now exact through local-callee scheduling. The standalone binding theorem
fixes the 15 `ExecuteSimpleTransfer` parameter cells, their reverse raw-cell/frame-binding order, copied
values, and the five readonly aliases at ordinals 9 and 11--14. The generated invocation evaluates its
receiver and 15 arguments and installs that frame in 64 microsteps/32 ticks; the actual block-entry wrapper
takes 65/33. `generated_execute_block29_to_simpleTransfer_entry_exact` composes this with the accepted
block-29 and non-null block-31 paths in 312/143 cold or 306/141 cached. The gas argument at ordinal 10 is
the by-value modeled result derived at the original `nextCell + 1`; the other 14 bindings and reads are
preserved from the original caller through the fresh prefix and symbol filters. The endpoint queues
function 24 block 0 with the exact local-resume, `setLast`, and block-32 completion continuation. It has
not executed the callee entry tick, body, state changes, or later `Refund` suspension. The success theorems
assume successful reads for all 15 argument locations, including the five readonly aliases, in addition to
frame, binding, freshness and signed-fuel premises; no one-short exhaustion theorem is claimed. The bare
binding theorem has frame freshness but no cell-freshness premise, so only the composed theorem establishes
the old-lookup preservation used at this boundary.

Generated `ExecuteSimpleTransfer` entry is now exact through completion of its first
`Metrics.IncrementEmptyCalls()` statement. The empty function-24 block 0 enters and schedules block 1 in
two microsteps/one tick. Under an exact leading `.incrementEmptyCalls` request with a unit reply, block 0
plus block 1's first operation takes eight microsteps/four ticks: it consumes that exchange, records one
request, restores the operand tail, sets the callee carried value to unit, and stops before the
`tx.ValueRef` reference assignment begins. Composition from generated `Execute` block 29 takes 320/147
cold or 314/145 cached and adds only the successful metric exchange and adjusted signed-fuel premise to
the inherited obligations. The request models normal invocation completion; it does not prove that a
concrete counter changed. Production may return immediately when execution metrics are disabled, and the
enabled path's thread-selected atomic update, CLR implementation, response adapter, public-entry
reachability and full pipeline relation remain outside these theorems. No zero-fuel or one-short theorem is
claimed at this boundary.

The rest of function 24 block 1 is now exact as a generated-interpreter transition. Starting from the
accepted post-metric endpoint, `generated_execute_simpleTransfer_block1_remaining_exact` executes the
readonly `tx.ValueRef` initializer, the value-transfer, sender/recipient, tracing and out-of-gas locals,
and the EIP-8037 branch in 55 microsteps/24 ticks. It allocates six cells: the `value` local aliases a
separate readonly snapshot cell, followed by four Boolean locals; five cells are bound in the frame and
the snapshot is not. `generated_execute_block29_to_simpleTransfer_block2_exact` composes the enabled
standard-domain path in 375/171 cold or 369/169 cached and stops with block 2 scheduled, before its
`hasValueTransfer` guard executes. The sender comparison uses the actual callee `recipient`, not an
unstated equality with `input.tx.recipient`. The local theorem uses natural-number interpreter fuel; the
outer composition retains the signed-`long` bound. No one-short theorem is claimed.

This is not yet a production heap-refinement result. Production `Transaction.ValueRef` borrows the
mutable `_value` field, while Stage B creates a readonly snapshot; their stability and heap relation remain
open. Canonical 20-byte address equality, stable normal-return spec/tracer getters, the metric-response
adapter, public-entry reachability and complete C#/Lean/CLR interpreter/pipeline refinement also remain
open. Block 2 and all account lookup, state-gas and value-transfer effects are subsequent obligations.

The following guard/query slice is also exact. Function-24 blocks 2 and 3 each take five microsteps/two
ticks, short-circuiting zero-value and self-send execution to block 6 without an account query. A nonzero
transfer to a distinct recipient schedules block 4 after ten/four. Block 4 consumes and records exactly one
matching `.isDeadAccount recipient` Boolean exchange in 13/six, then schedules block 5 for a dead account
or block 6 otherwise. The composed block-29 roots therefore take 380/173 cold or 374/171 cached for zero
value, 385/175 or 379/173 for either nonzero guard endpoint, and 398/181 or 392/179 through the account
reply. Every endpoint stops before its scheduled successor executes; block 5's state-gas charge and later
balance changes are not included. The queried path explicitly requires the canonical processor receiver
and uses the actual callee recipient.

The account reply is an oracle boundary. Production `StateProvider` may update cache/read bookkeeping and
`TracedAccessWorldState` records a BAL account read while consulting current overlays. These effects and
the truth of the emptiness result are not represented by the Stage-B exchange. Provider/cache/BAL
correctness, account/address representation, one-short and malformed-reply theorems, block-5 gas behavior,
and the complete production refinement remain open.

Function-24 block 5 is now exact through the new-account state charge. The generated path calls the local
cost getter and local `TryConsumeStateGas` body; its `.stateCharge` leaf computes the extracted fixed-width
kernel result and does not consume an oracle response. The success path takes 159 microsteps/73 ticks and
writes all five gas fields sequentially through a ref alias to `ExecuteSimpleTransfer`'s by-value gas copy.
The OOG path takes 94/43, writes no gas field, and sets `newAccountOutOfGas` to true. Both allocate three
cells and two retained frames, restore the outer operand tail with `returned = none`, preserve tape,
requests and statics, and stop with block 6 scheduled. The original enclosing `Execute` gas cell is not
mutated.

Composition from block 29 through a dead-account reply takes 557/254 cold or 551/252 cached on successful
charge and 492/224 or 486/222 on OOG. `commonDomain` pins the modeled new-account cost to 183600. These are
exact generated-interpreter/fixed-width trace equations, not a natural-number EIP refinement: production
representation and reachability still need the existing `Represents`, `WellFormed` and no-overflow bridge
obligations. Block 6, balance transfer/account creation, later execution-gas forfeiture, settlement and the
full pipeline remain outside this checkpoint.

Function-24 blocks 6, 7 and 8 are now exact in five microsteps/two ticks each. Their conditional composition
reads only guards that are reached: self-send schedules block 14 in 5/2, a distinct-recipient OOG schedules
block 14 in 10/4, and an eligible distinct transfer schedules block 9 for nonzero value or block 10 for zero
value in 15/6. The block-8 zero-value edge retains the generated `enteringRegions = [2]` metadata. Exact
preservation roots keep cells, aliases, bindings, captures, receiver, statics, tape, requests, returned value
and allocation counters unchanged; only the caller's carried Boolean, scheduled work and fuel advance.

Composition from block 29 through the accepted state charge and these guards takes 572/260 cold or 566/258
cached on successful charge and stops before `PayValue` in block 9. OOG takes 502/228 or 496/226 and stops
before block 14's tracing branch, so execution gas has not yet been cleared. The proof transports the retained
sender/value cells and reads the updated OOG cell without adding an oracle or gas premise. These remain
conditional generated-interpreter equations: region/CLR scope semantics, balance and account effects,
provider truth, production reachability, heap representation and full pipeline refinement remain open.

The tracing-disabled OOG suffix is now exact through the local `ClearExecutionGas` call. Blocks 14 and 16
each take 5/2, block 17 including the real local body takes 31/14, and their composition takes 41/18. The
endpoint schedules but does not enter block 18, retains the one writable alias cell and local-call frame,
sets only the callee's by-value gas copy's `Value` to zero, and preserves its four state-gas fields and the
true OOG local. Tape, requests, statics and the operand tail are unchanged; both retained frames carry the
interpreter's unsigned-zero void result and `returned` is empty. From block 29, the complete accepted OOG
route therefore takes 543/246 cold or 537/244 cached. Tracing-enabled action reporting, block 18 and later
settlement, CLR alias correspondence, production reachability and the full processor refinement remain open.

Under the pinned EIP-7708-disabled domain, block 18 is exact through logs initialization and the first
short-circuit guard. It takes 16/7, adds one direct model cell containing the generated typeless null
`.null ""`, allocates no frame, and schedules block 24 without entering it. The disabled edge has the exact
`enteringRegions = [4]` metadata; region captures are not eagerly allocated. Composition from block 14
takes 57/25 and from block 29 takes 559/253 cold or 553/251 cached. The retained clear-call alias/frame,
execution-zero gas value, four state-gas fields and true OOG local are preserved. Enabled EIP-7708 logging,
block 24 and settlement, CLR null/allocation correspondence and full production refinement remain open.

Blocks 24--26 are now exact through constructor-argument selection. Block 24 takes 41/17 to overwrite
captures 3--8 with empty bytes, signed zero, typeless null, the logs value and two false Booleans; blocks
25/26 each take 7/3 to set capture 9 to typed `OutOfGas = 4` or `None = 0`. Their conditional composition
takes 48/20 and stops with block 27 scheduled, before the `TransactionSubstate` constructor or logger read.
Captures are ordered 9, 8 through 3, then all old captures except the replaced IDs; no absence premise is
used. The OOG route takes 105/45 from block 14 and 607/273 cold or 601/271 cached from block 29 while
preserving heap cells, the clear alias/frame, logs, OOG and gas state, tape and requests. Constructor/logger
semantics, block 27 onward and the production/CLR refinement remain open at this boundary.

Block 27 is now exact for the selected null-logs, `ShouldRevert = false` path. Constructor entry takes
41/22, reads the canonical processor logger without an oracle request, allocates the caller local, writable
receiver and eight direct by-value parameter cells, and allocates one constructor frame. Function 22 then
executes blocks 0, 1, 2, 3, 5, 6, 7, 8 and 13 in 135/61, including empty-journal coalescing, all eight
modeled field values and captures 1, 2 and 0. The unticked local resume, conversion, assignment, `setLast`
and block finish take 5/0, so the complete block is 181/83 and stops with block 28 scheduled but unentered
through `leavingRegions = [4]`. The receiver and caller local contain the same completed model struct;
existing cells, gas/OOG/log state, statics, tape, requests and arbitrary tails remain preserved under the
explicit freshness and receiver premises. The Lean default substate has `Error` and `_logger` fields that
the C# Stage-B default initially omits, and the empty journal is an interpreter value rather than a real
`JournalCollection` allocation. Non-null logs, reverting/error-decoding paths, literal intermediate
cross-interpreter heap equality, CLR layout/allocation/reference identity and production-pipeline
refinement remain open.

The tracing-disabled edge through block 28 is now exact. The generated interpreter takes five
microsteps and two C# ticks, reads the existing tracing local as false, replaces every matching
caller frame with carried value false, and schedules block 32 without entering it. The theorem
preserves cells, statics, captures, gas and log state, requests, response tape, return state and
arbitrary machine tails. Independent source/IR review and executable sentinel replays cover direct
and aliased reads, duplicate caller-frame identifiers and the exact fuel boundary. Tracing-enabled
callback behavior, `Refund` suspension and execution,
production reachability and complete pipeline refinement remain open.

Block 32 is now exact through its first two intrinsic-gas copies. Entry plus the `FloorGas` and
`Standard` initializers takes 19 microsteps and nine C# ticks (1/1 + 9/4 + 9/4). The endpoint
prefixes the standard and floor cells at `nextCell + 1` and `nextCell`, records the same binding order
in every matching frame, carries the standard value, advances `nextCell` by two and preserves statics,
tape, requests, returned value, `nextFrame` and arbitrary tails. The theorem is conditional on the
entry frame and function symbol, target absence, old-cell freshness, receiver binding and reads, and
the signed-fuel bound. It leaves the `GetStateReservoir` initializer, `setLast`, `Refund` initializer,
second `setLast` and block finish queued and unentered. Production reachability and the CLR/heap bridge
remain open.

Block 32 is now also exact through the `GetStateReservoir` initializer and its paired `setLast`.
The reservoir phase takes 23 microsteps and ten C# ticks, so composition with entry and the two
intrinsic-gas copies takes 42/19. The exact endpoint prefixes a readonly gas alias and finalized signed
reservoir cell above the standard and floor cells, retains a completed fresh getter frame, installs the
reservoir binding and carried value in every matching caller frame, advances `nextCell` by four and
`nextFrame` by one, clears `returned`, and preserves statics, tape, requests and arbitrary tails. Its
additional premises are old-frame freshness, gas binding/readability and the `StateReservoir` field
projection. Work begins with `Refund` evaluation, followed by its `setLast` and block finish; none of
those tasks has executed. Production reachability, the CLR/heap bridge and later pipeline refinement
remain open.

`generated_execute_simpleTransfer_block32_refund_suspends_exact` extends that checkpoint to the typed
external `Refund` suspension in exactly 99 microsteps and 48 C# ticks from block-32 entry, composed as
42 + (56 + 1). The host call remains on the existing caller frame and carries exactly twelve
receiver-excluded operands in ordinal order: transaction, header, spec, options, readonly substate,
readonly gas, readonly opcode gas price, unsigned zero, readonly floor gas, readonly standard gas,
signed reservoir and false. The newly declared `spentGas` cell remains at its default unassigned value
at the original `nextCell + 4`; the `Refund` body/reply, outer `spentGas` assignment, following `setLast`
and block finish have not executed. The theorem derives this endpoint from original-state frame,
absence, freshness, binding, read, location-resolution, reservoir-field and fuel-bound premises, and its
axiom census is only `propext`, `Classical.choice` and `Quot.sound`. Production dispatch/reachability,
the CLR heap/interpreter bridge, the `Refund` implementation and settlement, and the full execution
pipeline remain open. This checkpoint found no production bug and is not a claim that Nethermind or
the complete pipeline is formally verified.

`generated_execute_simpleTransfer_block28_false_to_refund_suspends_exact` composes the
tracing-disabled block-28 branch with that checkpoint in exactly 104 microsteps and 50 C# ticks:
5 + 99 microsteps and 2 + 48 ticks. From original-state premises only, it derives the false carried
value and normalizes every matching caller-frame ID through the interpreter's all-matching
`replaceFrame` semantics before entering block 32. The endpoint is the same exact receiver-excluded
twelve-operand suspension, with `spentGas` still default and unassigned at the original `nextCell + 4`;
the `Refund` body/reply, outer assignment, following `setLast` and block finish remain unexecuted. Its
axiom census is only `propext`, `Classical.choice` and `Quot.sound`. Production reachability, the CLR
heap/interpreter bridge, `Refund` implementation and settlement, downstream execution and the full
pipeline remain open. This composition found no production bug and is not a claim that Nethermind or
the complete pipeline is formally verified.

`generated_execute_simpleTransfer_block28_false_to_refund_boundary_refines` adds a separate,
two-phase bridge to that exact 104/50 suspension. Phase one retains the same twelve operands, including
their locations and provenance. Phase two maps all five `Runtime.Gas` fields exactly into the accepted
ordinary-Refund input, requires explicit source observations over the same opaque transaction, spec and
options objects for fields absent from `Runtime.Input`, and reuses the accepted source-attached Refund
adapter premise. Its result boundary encodes exactly `SpentGas`, `OperationGas`, `BlockGas`,
`BlockStateGas`, `MaxUsedGas` and `GasRefund`; the pure adapter theorem also preserves the incoming
caller gas. This is a mathematical association with the suspension, not execution of its continuation:
the suspension remains terminal, and no `Refund` body/reply, result assignment, `PayRefund` or other
world-state effect, caller resume, block finish or later block is proved. Production reachability and
the CLR/heap/interpreter bridge remain open, so this result does not verify the transaction processor,
the execution pipeline or Nethermind as a whole.

`generated_execute_simpleTransfer_block28_false_to_standard_mainnet_refund_boundary_refines`
attaches that exact Stage-B receiver value and Refund boundary to either of the two admitted sealed
standard leaves: `EthereumTransactionProcessor` or the BAL
`TransactionProcessor<EthereumGasPolicy>`. Neither leaf declares its own `Refund` or `PayRefund`, so
both slots resolve to `TransactionProcessorBase<EthereumGasPolicy>`. The source artifact also pins the
containing-instance call from `ExecuteSimpleTransfer` to `Refund` and the containing-instance call from
`Refund` to `PayRefund`, together with the source and reference closures and the exact Stage-B prefix
and ordinary-Refund upstream manifests. This remains conditional on a finite runtime concrete-type
observation relating the exact Stage-B processor receiver value to one of those two leaves; it does not
derive runtime selection. Fresh source extraction, deterministic artifacts and mutations of leaf
sealing/slot ownership, lineage, either containing-instance call, IR, Lean or manifest bytes fail
closed, and the focused gate requires at least 556 discovered Stage-B tests. The theorem excludes
`SystemTransactionProcessor`, Optimism and Taiko processors, DI or plugin selection, host execution,
the `Refund` return and caller assignment, `PayRefund` effects, caller resume and all downstream
blocks.

`generated_execute_simpleTransfer_refund_result_install_exact` now separates the retained caller
continuation from the terminal suspension. From the same original-state obligations, 103
microsteps/50 C# ticks reach the exact machine immediately before the external `Refund` call; the
104/50 suspension theorem remains a separate terminal execution. Removing that call task and its
thirteen operands exposes the retained assignment, `setLast` and block-finish continuation. Given an
explicitly supplied value equal to the accepted six-field `RefundBridge.resultValue`, exactly two
additional interpreter microsteps and zero C# ticks assign it to the existing `spentGas` cell at the
original `nextCell + 4` and update the matching caller frame, leaving block 32's `finishBlock` task
pending. The installed state preserves allocation counters, statics, response tape, requests and
returned state. The unchanged result-installed machine remains terminal under the frozen prefix: its
next microstep rejects `"missing suspension"`. The continuation milestone therefore uses a separate
source admission that checks the exact four-operation source block 32 and its regular edge to block 33
entering region 5, the empty source block 33 and exact `WhenFalse` local-reference branch on
`newAccountOutOfGas`, and the region-5/capture-10 metadata. The paired source mutation that inserts a
fifth post-`Refund` operation remains accepted only as inert prefix evidence and fails this exact
continuation shape.

`generated_execute_simpleTransfer_refund_rearmed_to_block33_exact` changes only the pending block-32
finish task to the admitted residual program. Its result-installed wrapper starts with the entry indexed
at `fuel + 1`; block-32 finish and block-33 entry then take exactly two interpreter microsteps and one
C# tick and leave the condition evaluation pending with residual `fuel`. A second source admission pins
the exact block-34/35 `FlowCapture` nodes, static byte `StatusCode.Failure = 0` and
`StatusCode.Success = 1` fields, capture 10's explicit value mode and both regular edges to block 36.
`generated_execute_simpleTransfer_postRefund_block33_to_status_exact` evaluates the readable guard,
selects the `WhenFalse` arm, writes capture 10, performs `setLast` and schedules block 36 in 11/four from
entered block 33. `generated_execute_simpleTransfer_refund_rearmed_to_status_exact` composes the explicit
rearm in 13/five, and the result-installed wrapper proves the same endpoint from the accepted construction
indexed at `fuel + 5`. The interpreters now model block 36's implicit byte-to-Int32 conversion and
zero-initialized `int` local explicitly. `generated_execute_simpleTransfer_postRefund_statusCode_exact`
proves declaration, conversion, assignment, `setLast`, and the regular edge in 11/five from scheduled block
36. The composed entered-block-33 theorem takes 22/nine, and the explicit-rearm and result-installed wrappers
take 24/ten from states indexed at `fuel + 10`. Their endpoint schedules block 37 with `statusCode` and the
carried value equal to `.enum "int" 0` on OOG or `.enum "int" 1` otherwise. Capture 10 remains in the
interpreter frame after leaving region 5; this is administrative state and not a CLR lifetime-cleanup claim.
The accepted phases remain separate: 103/50 to the pre-call machine, 2/0 for conditional external-result
installation, and 24/10 after explicit rearm. This does not execute the host `Refund` call, prove `PayRefund`
balance effects, update transaction fees or headers, run `FinalizeTransaction`, complete the transaction,
or prove later pipeline processing. `generated_execute_simpleTransfer_refund_accessGuard_source_admitted`
pins block 37's exact `tracer.IsTracingAccess` property, parameter receiver, empty operation list,
`WhenFalse` condition, and regular edges to blocks 38 and 39.
`generated_execute_simpleTransfer_postRefund_accessGuard_exact` starts with block 37 scheduled and proves
exactly seven interpreter microsteps and three C# ticks under explicit frame, receiver-binding/read, and
fuel premises. It normalizes matching caller frames, sets their carried value to the independent access
Boolean, and schedules block 38 when true or block 39 when false. Existing cells and the selected frame's
captures remain unchanged at this guard endpoint.

`generated_execute_simpleTransfer_postRefund_call_frontiers_source_admitted` pins both complete successor
blocks, including the containing-instance receiver, exact argument bindings and passing modes, block 38's
regular edge to 39, and block 39's `FinalizeTransaction` branch value and return edge leaving region 1.
`generated_execute_simpleTransfer_postRefund_callFrontier_exact` evaluates the receiver and four access-report
arguments from scheduled block 38 in 21 microsteps/12 C# ticks, or the receiver and eleven fee-call arguments
from scheduled block 39 in 49/26. Both endpoints leave the invocation's `apply` task pending, preserve caller
cells and captures under matching-frame normalization, retain readonly argument locations without reading
their payloads, and preserve the pending caller continuation. The premises identify the frame, readable
receiver, argument bindings and value reads, and sufficient representable fuel.
`generated_execute_simpleTransfer_postRefund_accessGuard_to_callFrontier_exact` composes the guard with
these frontiers from scheduled block 37: access tracing enabled takes 28/15 and disabled takes 56/29, retaining
the guard's carried Boolean. Two independent exact-source cases and fifteen compile-valid source mutations
cover call receivers, argument identity and readonly temporaries, operation order, finalization arguments,
and return control flow. The existing replay still ends at `Refund` and does not execute these residual paths.

`generated_postRefund_feeHelper_entry_source_admitted` pins the actual eleven-parameter
`UpdateHeaderGasUsedAndPayFees` signature and its empty CFG entry with the regular edge to block 1.
The prefix artifact retains that signature and the helper's complete source CFG as inert evidence;
the frozen replay and its executable function roster remain unchanged. A purpose-specific residual
program admits only the helper's empty entry, not its guard or body.
`generated_postRefund_feeHelper_bind_exact` proves exact receiver and argument binding into a fresh
frame: six by-value cells and five readonly aliases, in source order, with exact symbols and locations.
`generated_postRefund_feeHelper_cell_layout` fixes the eleven consecutive cell identifiers and owners.
`generated_postRefund_feeHelper_call_entry_exact` applies the false-access fee-call frontier in one
microstep, then enters its empty block in a second microstep/one C# tick. The endpoint has the entry's
`finishBlock` task pending before the counter-participation guard, and retains the caller's resume and
finalization continuation. Explicit fresh-frame/fresh-cell and representable-fuel premises ensure that
existing readable cells stay readable with the same values; old cells, the prepared caller frame tail, statics, host requests,
response tape, and returned result are retained. One source-admission case, four compile-valid source
mutations, and five re-signed inert-metadata controls cover this boundary.

`ReportSimpleTransferAccess` and its tracker/pool/callback behavior, actual `PayFees` execution,
`FinalizeTransaction`, and later pipeline processing remain open. These entry roots do not compose the
explicit-rearm/status-code phases into uninterrupted transaction execution or establish the concrete
tracer-getter/CLR bridge. The automated axiom gate checks seventy-five exact theorem roots transitively,
allowing only `propext`, `Classical.choice` and
`Quot.sound`, with injected-axiom and malformed-output negative controls.

### Projected EIP-8037 fee-helper residual

`StageB/FeeHelperSource.lean` independently reconstructs the selected helper CFG blocks and proves
`FeeHelper.selected_source_admitted`: block 1's exact routing call on `opts` and `_parallel`, block 2's
EIP-8037 guard, block 3's unchecked execution add followed by state add followed by constrained
`CombineBlockGas` assignment to `header.GasUsed`, and block 5's exact receiver/ten-argument virtual
`PayFees` invocation. Source mutations cannot pass by changing only the handwritten residual.

`StageB/FeeHelper.lean` is a specialized executable residual, not an extension of the generic runtime.
Its `Objects` projection contains identities and only `_parallel`, both cumulative counters, and
`header.GasUsed`. `EntryWitness` explicitly joins the already accepted helper-entry machine to supplied
parameter/read/readonly-alias observations and externally observed mutable fields. The proof does not
derive a CLR reference heap from the immutable machine or claim generic `Runtime.steps` executes
`CompoundAssignment`. No assumption asserts the desired counter result; it is computed and proved
inside the projected residual. Concrete getter/setter/heap correspondence remains a separate obligation.

The source-pinned `SystemTransactionRoutingKernel` leaf supplies the exact SkipValidation bit (4) and
parallel gate. The existing `BlockReceiptGasAccountingKernel` supplies unchecked UInt64 addition and
source-validated `EthereumGasPolicy.CombineBlockGas`; the gate checks its supporting sources as well
as its source/IR/Lean hashes. `natural_counter_refinement` reuses its accepted no-wrap theorem to prove
natural execution/state sums and header max, with explicit input-range and no-wrap premises.

`StageBEffectiveBlockGasExtractor` admits the live, semantically bound positional UInt64 getter and
emits the narrow `StageB/Leaf/Generated/EffectiveBlockGas` artifacts. It preserves
`BlockGas > 0 || BlockStateGas > 0 ? BlockGas : SpentGas`: zero execution with nonzero state stays zero.
Current source, compiler pins/references and extractor identities are recorded and regenerated for
byte-exact checks. No stale Stage-A completion artifact is imported or regenerated. Its discovered
schema/source-acceptance/assembly-identity drift is verification-artifact drift, not a Nethermind bug.

`projected_residual_reaches_pending_payFees` proves exactly seventeen specialized transitions from
entry to the pending fee apply: entry, routing guard, fork guard, execution add, state add, header
write, receiver, then ten arguments. These are not generic-runtime microsteps or production C# ticks.
`accepted_entry_reaches_pending_payFees` and `entry_and_residual_compose` compose only through the
explicit witness, retaining the earlier checked two-step helper entry. `one_short_fuel` fails with
sixteen residual fuel units. `pending_frontier_exact` fixes receiver/argument order, the four readonly
alias locations/provenance and six value arguments, preserves the underlying entry machine, and
proves the residual refuses to apply `PayFees`. The three writes are separately logged at the correct
processor/header identities; header max uses both already updated, possibly wrapped counters.

Seven executable theorem groups cover eight literal counter/wrap/max cases, eight raw option masks,
the three excluded paths and one-short fuel, ordered writes, four alias identities, paid rather than
effective fee gas, and a nested readonly alias observation. These are projected runner vectors, not
end-to-end production executions. Ten compile-valid helper mutations cover bit-test, parallel,
counter identity/order, fallback, max-versus-sum, update order, paid gas and fee placement. Twelve
narrow-leaf tests cover current artifacts, seven compile-valid property mutations and four artifact/
hash/roster controls. The focused Stage-B discovery floor is 584.

Skipped-counter, parallel, and EIP-8037-disabled paths return `outside` in this milestone rather than
claiming production behavior. PayFees dispatch/effects, FinalizeTransaction, generic mutable heap and
compound assignment, CLR execution, and uninterrupted transaction composition remain unproved.
The accepted prefix/entry theorem and frozen replay endpoint are unchanged.

### Projected standard-mainnet PayFees and caller return

`StageBPayFeesExtractor` admits the current protected virtual ten-parameter `PayFees` signature, exact
body token structure and typed Roslyn body/CFG. It follows the sealed `EthereumTransactionProcessor`
and `TransactionProcessor<EthereumGasPolicy>` lineages to the base slot and rejects both overrides
and shadowing. `SystemTransactionProcessor<EthereumGasPolicy>` has a separately pinned empty override
and is excluded; Optimism, Taiko, Xdc and plugin leaves are also excluded. Concrete DI/runtime receiver
selection is an explicit observation, not inferred from the model receiver enum.

The fresh narrow generated arithmetic/program leaf is a source-admitted transcription, not a generic
C# compiler. `arithmetic_refines` relates its formulas to independently written modular formulas over
bounded UInt256 words and UInt64 paid gas; `modular_outputs_bounded` proves every computed monetary
quantity fits UInt256. The effective base fee is `min(header.BaseFeePerGas, effectiveGasPrice)`.
Free transactions zero EIP-1559 fees; EIP-1559 enables their collector component; the conjunction of
blob support and the blob-collector flag adds the blob component modulo 2^256. `ReportFees` receives
premium and `(eip1559Fees + blobBaseFee) mod 2^256`, independently of collector gates.

`projected_payFees_exact` executes ten projected source stages, including the unconditional destroy-list
probe, then returns void. The pinned simple-transfer empty destroy list makes the beneficiary eligible
even for zero premium; the final `IsEip8037Enabled` disjunct is short-circuited. The existing helper-entry
boundary supplies the EIP-8037 context, actual paid gas and readonly premium/effective/blob aliases.
The witness also observes a null `_destroyList` through the actual substate argument; a separate theorem
proves the accepted simple-transfer constructed substate has that null field for both halt flags.
Other transaction/spec/tracer flags remain explicit observations. Actual provider credit/create and
tracer operations are abstract effects, with no distinct-address assumption or effect coalescing.

Reads and effects share one ordered event trace. Beneficiary and collector argument reads are separate
from earlier destroy/collector-null probes; later header, transaction, spec and tracer observations are
checked after preceding effects. `EventExecution` relates that trace to an arbitrary external world
transition relation and pointwise read relation. `event_execution_preserves_effect_order` proves its
effect-only projection agrees with the ordered abstract-effect execution. No provider balance law,
account-creation semantics, Boolean return interpretation, persistence or host exception behavior is
asserted. Normal-return effects and the retained interpreter-machine projection/noninterference are
explicit witness obligations, not assumed computed fee outputs.
Read witnesses are observational in the chosen world abstraction; mutating getters are outside this
normal-return boundary. Credit argument observations supply non-null addresses, including the second
collector getter read; the earlier null check is not silently treated as proof of getter stability.

`pending_boundary_composes` connects the accepted fee-helper frontier to projected PayFees completion.
`helper_exit_source_admitted` pins the helper's empty exit after its fee call. The projected normal-return
handoff uses the exact retained caller continuation (`handoff_uses_retained_continuation`); it is not
claimed to arise from generic execution of the fee/helper bodies. `generic_return_resume_cleanup_exact`
then executes three genuine generic-runtime steps: void resume, expression-statement cleanup, and
caller `setLast`. `payFees_and_caller_resume_compose` combines these only through the explicit normal
effect/read/noninterference witness. Cells, request/tape, allocation counters and C# fuel are retained,
and all-matching duplicate frame replacement is preserved. This theorem stops with `FinalizeTransaction`
scheduled for evaluation; the separately checked argument-only continuation below does not enter its body.

Nine vector groups cover ten literal arithmetic/min/free/collector/wrap cases, same-account credits,
zero credits, collector/tracer branches and repeated getter observations, exact event interleaving,
short-circuited reads, projected ten/one-short fuel, excluded nonempty destroy lists, zero-C#-fuel
generic return, and duplicate caller-frame semantics. Twenty-four C# cases cover fourteen compile-valid
body mutations, three dispatch mutations, two read/effect reorderings, canonical admission and four
re-signed artifact/roster controls. The gate checks six extraction modes, at least 584 discovered
Stage-B tests and seventy-five exact axiom roots. Prefix/entry artifacts are unchanged; only compiler
source evidence in the control manifest changes when adding the new extractor/CLI.

The broad OrdinaryEvmCompletion fee lemma is private and is not reused through its larger bundle.
Concrete virtual dispatch/DI, UInt256 optimized implementation, external object/provider/journal/tracer
semantics, host exceptions, the access-report branch and uninterrupted end-to-end composition remain
outside this milestone. No production Nethermind bug was confirmed.

### FinalizeTransaction binding and empty entry

`StageBFinalizeEntryExtractor` is a separate source-admission mode (`--stage-b-finalize-entry`). It uses
the current StageBLowering/compiler closure and checks the production method's private, nonstatic,
nonvirtual ownership, twelve ordered parameters, full equality to the accepted caller descriptor,
entry position (0,0), empty reachable Entry0, and regular edge to block 1 with no entering, leaving or
finally regions. The three `StageB/Finalize/Generated/FinalizeEntry` artifacts pin the current compiler,
sources and extractor/emitter closure; `StageBArtifact.cs` contains the shared Lean data emitter and
is explicitly pinned/tested. No finalization body is added to the frozen prefix, and no stale Stage-A
bundle is imported. The residual function contains only Entry0, not block 1.

The runtime exposes its existing `createFrame` and `bindCallArguments` definitions plus the factored
single-parameter `bindFrameParameter` operation. `bindFrameParameter_copy_or_alias` gives a narrow
one-cell equation without reading alias payloads. `Finalize/Binding.lean` derives an arbitrary-length
mixed-copy/location binding theorem from that actual fold, retaining duplicate-symbol behavior; it
does not implement a second twelve-argument evaluator. The two small `parameterDefaultValue` and
`operandReadResult` wrappers support exact inactive alias payloads and successful old-read statements.
These are proof-package APIs, not production Nethermind APIs or a CLR heap bridge.

`Finalize.binding_exact` fixes ten copied values, including the complete six-field GasConsumed at
ordinal 10, and exactly two readonly locations at ordinals 7 (payment) and 9 (substate). It allocates
twelve consecutive cells and one fresh frame, retains receiver and source symbols/provenance, and
reverses raw cells and bindings while preserving old suffixes. It creates no readonly temporaries.
`bind_step` consumes one generic microstep and zero C# ticks, including at zero fuel.
`bind_empty_entry_exact` adds one entry microstep/tick, stopping with Entry0's finish task pending
before the Warmup guard is scheduled. `scheduled_to_empty_entry_exact` composes 53 steps/27 ticks
from the accepted scheduled invocation; `return_to_empty_entry_exact` composes 56/27 from the accepted
projected PayFees void-return handoff. Fresh frame/cell bounds and representable fuel are explicit;
duplicate caller IDs and arbitrary continuation/operand tails are retained. `binding_preserves_old_reads`
proves successful old operand reads survive allocation, without requiring either readonly payload to
be readable. `entered_preserves_old_reads` carries that result through entry, and
`entered_preserves_context` fixes all other retained machine fields exactly.

Twenty-eight C# controls cover canonical admission, six compile-valid source mutations, fourteen
descriptor/CFG projection mutations and seven re-signed artifact/hash/roster controls. Eleven Lean vector
groups cover ten distinct abstract copy sentinels, six unequal gas fields, unreadable chained/cyclic
aliases, exact reversed layout/counters/suffixes, zero-tick binding, one-tick entry/exhaustion, duplicate
caller IDs and nonempty tails, old reads, 53/56 exact compositions, 52/55 short microsteps and 26 short
ticks. Abstract sentinel payloads deliberately test positional binding independently of C# type-domain
correspondence. Warmup/body semantics, transaction property writes, world-state/receipt effects,
finalization return, concrete alias/default/heap/CLR correspondence and uninterrupted production
pipeline refinement remain open. No production Nethermind bug was confirmed by this milestone.

### FinalizeTransaction argument frontier

`Runtime.generated_finalize_call_frontier_exact` executes the exact source-pinned private, nonvirtual
invocation using the existing generic receiver and argument lemmas, for arbitrary programs and
continuations. It consumes 51 microsteps and 26 C# ticks: one invocation eval, two receiver steps,
and four steps for each of twelve arguments. Ten arguments require successful value reads. Payment
ordinal 7 and substate ordinal 9 require bindings only: their readonly locations, original roots,
field paths and provenance remain exact even if alias payloads are unreadable. `spentGas` is a
by-value copy of the complete six-field struct, not its implicit `ulong` projection.

`scheduled_finalization_arguments_exact` starts at the accepted PayFees return endpoint.
`return_to_finalization_apply_exact` includes the accepted three zero-tick return/statement/setLast
steps, reaching the same pending `.apply` in 54 microsteps/26 ticks. The argument theorem retains
all cells, frames (including duplicate IDs), statics, tape, requests, allocation counters and returned
value, plus arbitrary operand/work tails. Only the already accepted return cleanup normalizes the
matching caller frames. This argument-only theorem neither adds nor applies the callee; the separate
binding/empty-entry extension above starts from its pending application.

Eight executable vector groups distinguish exact/extra/one-short ticks, 50 versus 51 microsteps,
independently quantified Boolean positions, six unequal gas fields, readonly chains ending in missing/cyclic
payloads, nonempty continuations, duplicate IDs and the complete 54-step composition. Fifteen C#
controls cover canonical source/modes, eight compile-valid argument changes, two compile-valid
signature/virtual changes and four re-signed projection changes. The accepted prefix/dispatch/fee
artifacts remain unchanged. Callee body, finalization effects, concrete CLR reference
heap/provider semantics and uninterrupted production-pipeline refinement remain open.

Production-path review confirms that `PrepareSimpleTransferFastPath` returns the saved non-null `tx.To`
only after the candidate, code-empty and no-delegation checks, and that successful gas initialization then
selects this branch. The future adapter must relate the immutable 20-byte address to the model payload,
distinguish `Address.Zero` from null, establish truthful code-lookup results and earlier dispatch premises,
and encode the synthetic Stage-B authorization flag from `AuthorizationList != null`. It must not copy
`Transaction.HasAuthorizationList`, which also requires SetCode type and a nonempty list. Current source
guards and mutation tests pin the intended nullness expression, and no production-to-Stage-B adapter exists,
so this is a bridge obligation rather than a current executable mismatch.

Lazy `result` allocation describes the selected Stage-B boundary: production declared and assigned
that local earlier. Public-entry reachability must relate its dead prior payload to the model before
the overwrite. The quotient needed for C# null versus Lean zero inactive `TGasPolicy` payloads, raw
CLR heap equality, and the C#/Lean interpreter and production-pipeline bridges also remain open.
This checkpoint found no production Nethermind bug and does not establish full verification.

The generic Lean assignment branch now also matches C# ref-assignment ordering: it validates and
installs the source alias without reading the source, with a universal equation that permits an
unreadable source location. This equation is a handwritten-machine fact: cross-language composition
must additionally relate the target root to an actual C# `CellLocation` and exclude Lean's synthetic
readonly-temporary and discard roots, which can also have empty field paths. Ordinary assignment now
also follows C#'s separate pre-write and post-write source reads, and a universal equation exposes
both values instead of assuming the source is stable across the target write. C# discard writes remain
no-ops while Lean represents discard as a writable cell. Terminal replay observations omit raw cells,
but no theorem yet proves that the two frozen discard locations cannot be retained, aliased or reread;
a future reachable-heap relation must prove their writes unobservable or introduce a distinct discard
location.

The fixed emitted call graph now has a separate recursive structural admission predicate. Ordinary
kernel reduction proves that all 768 nodes are covered, including every operation root, optional
branch root and descendant; all 23 local-call targets exist and precede their callers in the frozen
26-function order; receiver, argument-child and parameter-ordinal indices are in bounds; and all 47
Invocation/ObjectCreation nodes carry complete unique descriptors. Descriptor-free local calls with
parameters have the C# positional arity, while zero-parameter calls may ignore surplus operands.
An independent whole-program predicate proves that parameter symbols are unique within every one of
the 26 functions; this is the static key invariant needed by the candidate frame's symbol-indexed cell
map. Together with the mixed theorem it supports the three distinct lookup keys proved there, but it
does not establish arbitrary descriptor-to-parameter contents, dynamic reachability or equivalence of
malformed-program failure order.

Successful `createFrame` and call binding now establish the new Lean frame's ID, function symbol,
receiver, unit carried value and empty captures while preserving work, operands, statics, tape,
requests, returned value and shared C# fuel. Exact apply equations schedule
`enterBlock :: resume` for valid local Invocation/ObjectCreation calls and cover missing bodies and
binding rejection. Three two-step theorems derive the callee frame lookup and compose the apply step
with first-block success, zero fuel or Outside, retaining the existing all-matching duplicate-ID
behavior. They start after child evaluation and assume successful binding for the success cases;
they compose with generic allocation/read-preservation and the exact mixed-cell theorem, but do not
prove arbitrary-list parameter contents, CLR heap/frame representation or arbitrary callee execution
and unwinding.

Compile-valid semantic mutants change the emitted definition and fail the independent proof;
unsupported arithmetic, named-argument reordering, enum/layout rebinding, initial-fuel changes,
ignored results, wrong/missing cursor writeback, ref aliases and extra effects fail the admission gate.
Independent reference-identity vectors cover all edge kinds with distinct source/destination ordinals,
direct Return/Suspend with non-Boolean values and missing edges, and an Outside block containing a
poison operation. The same poison is reached when the block is inside the selected domain.
Artifact admission byte-compares a fresh canonical extraction, so stale source inventories, unknown,
missing, duplicate or changed JSON fields and altered Lean output fail closed. The focused gate
requires Release, checks freshness and proof placeholders, builds the control refinements and runtime
theorems, and reruns the
shared replay and existing prefix regressions.
Local-return tests include all four independent decision rows, six candidate-interpreter paths
covering construction and all operand modes, and six replays of the actual admitted return suffix
with a receiver whose read throws. Injecting an eager read fails the same expected observation on
every no-read path; receiver-location identity and ordinary returned values are checked separately.
The no-Return matrix covers operation roots, branch-value roots and nested children with zero, one
and multiple children, requires rejection before fuel/request/tape consumption, and retains CFG,
local, constructor, static and suspension return-path regressions.
Transparent-wrapper tests cover all selector rows, every mode for Argument and DeclarationExpression,
ExpressionStatement materialization, poisoned no-read locations, alias identity, zero/two-child
arity rejection before a poison child, exact parent/child fuel boundaries, and compile-valid order,
dispatch, helper-input, lazy-read and identity-return mutations. The runtime theorems cover only the
176 emitted Argument, DeclarationExpression and ExpressionStatement nodes. Conversion and the
runtime-supported but absent Parenthesized kind remain outside them; concrete CLR `Operand.Read` and
Location representation correspondence is still an adapter premise.
An actual two-block Execute probe distinguishes the old carried value, two ordered operation values
and final branch value through four aliased locations, checks operation order, branch-last precedence,
one effective location read per evaluated root and exact fuel. A separate regression covers carrying
the last result through an empty-successor Return. Compile-first mutations reject
discarded operation/branch results, omitted or duplicated outer reads, stale values/operands, reversed
evaluation order, entry/lookup ordinal changes, Tick removal/duplication/reordering, late Outside,
missing branch evaluation, wrong initial carried value or block exit, and stale carried data passed
to `FinishBlock`. The corresponding Lean examples
reject wrong carried updates, retained popped operands, skipped reads and first-duplicate-only frame
replacement.

A direct probe of the actual private C# `Call` method makes 83 independent binding observations over
all six ref kinds, immediate/mutable/readonly/poison sources and explicit/descriptor-free calls. It
checks rejection before callee entry, aliasing without a source read, copy isolation, readonly
temporary value/provenance/write rejection, and positional copying of ref-like declarations. The
probe replaces only `Execute` at the callee boundary so the observed frame is produced by the real
binding loop. These executable observations support the handwritten equations; they are not a
cross-language representation or refinement theorem.

A second synthetic probe runs the actual recursive C#
`Evaluate → Invoke/ObjectCreation → Call → Execute → return selection → caller continuation` path
without intercepting `Execute`. Eighteen observations cover all ordinary/constructor operand modes,
receiver and argument evaluation order, ref writes, distinct callee-return and mutated-receiver
values, constructor-location identity, caller continuation, rejection/Outside/suspension and exact
shared-fuel boundaries. Seven behavior mutants independently disturb child order, argument binding,
receiver reads, callee execution/frame selection or constructor alias identity; both runtime
observations and source admission reject them. Exact positional records for Call, Target, Argument,
Member and Parameter and the associated enum layouts are now source-pinned. Missing-call timing,
malformed descriptor/index exceptions and exact diagnostics remain excluded.

The representation boundary used by those calls is now independently source-admitted rather than
being covered only incidentally by the enclosing `Machine` declaration. Exact declaration tokens and
semantic target bindings pin `Cell`, `Operand`, `Location`, `CellLocation`, `FieldLocation`,
`ValueLocation`, `DiscardLocation`, `StageBResolvedLocation` and `StageBValue`. Compile-valid
mutations cover value initialization, alias reads and writes, readonly propagation, identity,
field-path reads/writes, readonly-temporary behavior, discard writes and value fields. A separate
runtime probe observes alias roots, copy isolation, readonly failures, field updates and resolved
paths. This closes source admission for the concrete representation operations used here; it is not
the missing CLR-to-Lean heap relation. In particular, active aliases hide but do not reconcile
inactive wrapper payloads: C# defaults `TGasPolicy` to null where Lean constructs zero gas, and C#'s
default `TransactionSubstate` omits `Error` and `_logger` fields that Lean includes. General
default/value correspondence therefore remains open.

This is source-attached refinement of five formal-package control functions plus the admitted-program
no-Return and local-call-shape invariants, local jump/Return/local-resume simulations, and bounded
transparent eval/apply/setLast, block-entry, local-call-entry and local-return-to-caller scheduling
simulations only. The C# CLR
execution model, Roslyn binding/lowering and this restricted emitter remain trusted. No theorem yet
relates a complete recursive C# callee execution and unwind to Lean's explicit work stack,
parameter cells/aliases, operand stack, static cells, response tape, requests and residual fuel.
Reachable-machine frame/cell freshness and ownership, universal fuel adequacy, complete interpreter simulation and production Nethermind
pipeline refinement remain open. Replay is still finite differential evidence, not those theorems.

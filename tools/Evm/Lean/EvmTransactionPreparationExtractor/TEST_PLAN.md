# Test plan

The package tests are intentionally independent of production execution.

- Generate twice into isolated directories and compare IR, manifest, and Lean
  bytes.
- Change `zeroGas.value` in either imported ordinary-dispatch Lean model and
  assert exact dependency-byte rejection without output; compile both changed
  Lean files independently to establish that the mutations are valid programs.
- Remove either imported Lean file and require exact missing-dependency rejection.
- Change or remove the sibling manifest's Lean identity and require rejection.
- Remove the signed-state-gas arithmetic residual from IR and require rejection.
- Mutate the exact preparation-gas assignment and assert extraction fails
  without output.
- Relocate the legacy delegated-target fork conditional into the target-presence
  guard's false branch and assert its dedicated semantic gate rejects the
  compile-valid mutation without output.
- Add a preparation branch and assert the fixed CFG admission fails without
  output.
- Mutate the VM boundary parameter identity and assert the source boundary fails
  closed without output.
- Mutate the pinned Amsterdam EIP-8037 activation and assert the fork route
  fails closed without output.
- Mutate the exact `ValidateStatic` authorization-list guard and assert the
  static-validation route fails closed without output.
- Mutate the source `delegationRefunds = 0` initialization and assert the
  source-entry adapter admission fails closed without output.
- Mutate the source `StackAccessTracker` tracing argument and assert the
  source-entry semantic binding fails closed without output.
- Mutate the source `Snapshot.EmptyPosition` sentinel and assert the snapshot
  adapter fails closed without output.
- Mutate the exact EIP-7702 authorization-list snapshot guard and assert the
  snapshot-presence adapter fails closed without output.
- Mutate the nested EIP-8037 snapshot guard and assert the three-way
  snapshot-presence adapter fails closed without output.
- Mutate the exact `Transaction.Type == TxType.SetCode` component of
  `HasAuthorizationList` and assert the transaction-type admission fails
  closed without output; the reference also rejects SetCode CREATE with an
  empty modeled authorization list.
- Mutate either the exact `AuthorizationList is not null` or
  `AuthorizationList.Length > 0` component and assert the transaction
  authorization-list admission fails closed without output.
- Mutate either `ValidateStatic` failure guard (`!noCreation` or `!authList`)
  and assert the corresponding validation return path fails closed without
  output.
- Substitute a distinct tracker in the outer `ExecuteEvmCall` arguments and
  assert exact fresh-local-to-`RentTopLevel` access lineage fails closed
  without output.
- Substitute the `VmState.RentTopLevel` `accessedItems` argument and assert
  exact `ExecuteEvmCall.accessedItems` parameter identity fails closed without
  output.
- Mutate the serialized boundary formula and assert IR validation rejects the
  changed live semantics.
- Scan generated Lean for `sorry`, `admit`, and `axiom`.
- Audit all 128 exported Reference/Refinement theorem dependency closures with
  Lean's `collectAxioms`; accept only `propext`, `Classical.choice`, and
  `Quot.sound`. Require every inventory result, reject unexpected named exports,
  and run missing-export, missing-output, and forbidden-dependency controls.
- Enumerate the actual imported repository modules and all 3,565 originating
  theorem declarations, including 292 private and all compiler-generated
  descendants. Check exact module membership, per-module public/private counts
  and ordinal name-roster hashes, and collect every declaration's axioms.
  Reject inventory drift; missing, duplicate or malformed records; omitted or
  added modules; unlisted public/private/nested declarations; and explicit or
  native-decision axioms. Removing a private result must fail independently of
  the public-export checks. These checks do not rely on namespace prefixes.
- The handwritten reference vectors cover authorization partial OOG, delegated
  target OOG and precompile/warm no-charge paths, legacy delegated-target warming
  without the EIP-8037 charge or read (with hot/cold storage both enabled and
  disabled), dead-recipient OOG, CREATE collision, null code, and the VM
  boundary, including Warmup partial debit, sender/recipient and
  authorization-recipient aliasing, legacy physical-account code-insert
  refunds, preservation of the keyed authority balance without pre-frame
  recipient credit, and rejection when a fixed-width gas witness disagrees
  with the actual post-nonce handoff gas. CREATE-domain coherence rejects a
  delegated-target fact because the source CREATE branch never resolves
  recipient code. Entry-domain negative vectors reject
  a handoff with a pre-warmed target and any nonzero initial delegation-refund
  counter while internal delegated warm/refund lemmas remain available.
- Reachable CFG evidence is emitted for the transaction boundary, static
  validation, authorization loop/validation, environment, top-level call, and
  deployment helper, including normal/exceptional exits and back-edges.

The C# suite, deterministic fresh generation, and Lean checks are serialized by
the parent task. This package does not run those commands before release of the
validation lane.

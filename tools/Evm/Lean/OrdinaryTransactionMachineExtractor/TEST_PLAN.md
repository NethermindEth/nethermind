# Static verification and mutation plan

No dotnet, Lake, or Lean command has been run while the serialized transaction-preparation lane
is owned elsewhere. The test project is additive and is intended for the later gate.

The test vectors cover:

- empty input and a nonzero incoming tracer index/history, proving the fresh adapter reset is
  explicit and not inherited from a caller seed;
- valid one- and two-entry prefixes, receipt indices `0,1`, cumulative gas history alignment,
  `MarkAsSuccess` append, and `EndTxTrace` index increment;
- invalid-first, malformed-first, malformed-second-after-valid-prefix, false-result and truthy
  `EvmException` entries, with unchanged receipt/gas histories at the rejected entry;
- receipt-index and gas-total mutation, fresh index/current-tracer/history/cumulative-gas reset,
  successful status/append mutation, missing/extra per-entry normality, and Start/Execute/End
  adapter reordering, a false proof-carrying normal-return field, a settled-entry/proof mismatch,
  both nested/current receipt-forwarding predicates, and all four forwarding event combinations;
- conditional `StartNewBlockTrace`, conditional pre-commit, early return after fold before
  `TransactionsExecuted`, and early return after callback before post-commit;
- BAL guard true-arm bypass, gas/invalid-result/BAL-inner return bypass, `Enabled` mutation,
  direct-inner/decorator DI mutation, and exact-base/non-virtual executor selection mutation;
- all three simple-transfer eligibility guards, no-code/delegation classification, exact-Commit
  execute/extension routing, execute-adapter factory/registration/construction, and every link of
  the block-context propagation route including exact constructor types, the `BlockProcessor`
  executor-field capture, standard validation-module registration, and decorator ordering;
- source path/owner/member relocation into a local function or lambda, unresolved operation,
  CFG reachability/dominance/post-dominance, changed/missing/ambiguous compiler reference,
  assembly/SHA/MVID/dependency/order mutation, and stale artifact hash;
- compile-valid exact-identity mutations for method relocation, an unledgered competing overload,
  a same-simple-name method, constructor aliasing, field/local and enum shadows, the canonical base
  type argument, a fluent generic type-argument alias, and redirected fluent and ordinary
  invocation targets.

The current serialized test gate requires at least 115 discovered NUnit cases. An unmutated
`ExtractForTest` baseline must complete before the exact identity mutations are meaningful; each
identity mutation asserts its full, anchor-specific diagnostic so an earlier binder cannot mask
the intended constructor, enum, base, fluent, generic-argument, or invocation gate. The proof-shape
checks require each normal-return field to prove its corresponding Boolean and
the per-entry chain to align positionally with the adapter-supplied settled list. The Roslyn tests
use compile-valid source overrides, assert the local extractor diagnostic (so a
stale settled-artifact gate cannot mask the mutation), preserve the original source bytes, and
require an empty output directory for every rejected mutation. The generated Lean check must
reject theorem/lemma/example/axiom/sorry/admit additions,
whole-result equality, imports that bypass the pinned ReceiptTerminal kernel, and a claim broader
than the adapter-supplied `OnlyOkTerminal` fold. The later serialized gate must run the extractor
twice into scratch directories, compare IR/manifest/Lean bytes, then publish the three artifacts
atomically and directly check generated, all four specification files, refinement, vectors, and
the test project.

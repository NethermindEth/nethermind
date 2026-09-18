# Audit-scaffold test plan

The focused suite covers:

- exact embedded admission and case-sensitive repository path resolution;
- source/member/dependency mutation rejection;
- namespace, nested owner, generic arity, empty-or-canonical parameter,
  syntax-kind, and member token-hash admission, including malformed-line
  rejection;
- AST-derived recognized control topology (conditions, parent/arm edges, returns, gotos,
  labels, try/catch/using nodes, and finally nodes when present), exact
  cancellation statement-site cardinality with wrong/extra-site rejection,
  and required branch bindings;
- all four identity-pinned refinement dependencies and their theorem names;
- deterministic IR, source manifest, generated Lean, and combined digests;
- theorem-free generated imports, consumed source-derived plan, and the
  absence of recursive whole-run names;
- independent reference/refinement presence and no sorry/axiom;
- all dispatch subjects, child/top-level routes, precompile failure routes,
  cancellation/escape, cleanup, and create-deposit classifications;
- finite fuel zero/positive semantics and the absence of leaf calls at zero.

Verification is intentionally package-local. It must not build the parent
Evm.slnx or modify parent imports, global manifests, task cards, or production
C#.

The default package and aggregate gates select all 22 accepted Stage E cases,
require no failures or skips, and build the five explicit Stage E Lean targets.
The six `Operational_*` draft cases remain intact and are included by the
standalone `-Operational` gate; they do not contribute accepted Stage E evidence.

# Stage F operational gates

The Stage E focused suite remains the legacy one-step gate. Stage F adds a
separate invocation (`--stage operational`) and must be checked independently:

1. Build the extractor with warnings as errors and run the package tests.
2. Generate the three operational artifacts twice into isolated directories;
   require byte-identical IR, source manifest, and Lean output.
3. Mutate an admitted source/member/compiler/dependency/theorem identity and
   require extraction to fail before writing artifacts.
4. Check that generated Lean is theorem-free, imports no reference module,
  consumes typed extracted plan/topology, the executable control-instruction
  stream, and exact route tables, and contains
   an explicit recursive finite-fuel loop with all four dispatch modes, exact
   1024-op nonterminal cancelable epochs plus cancel polls, the exact
   non-cancelable terminal chain count, child push/resume, settlement, and
  cleanup paths. Mutate a source-bound typed control predicate/effect and a loop semantic
  entry so the generated interpreter/guard changes or fails closed. Check
   route/PC cardinality and byte alignment as adapter-supplied evidence only;
   valid traces may revisit PCs; exact per-op successor/control behavior,
   including PUSH widths, remains an explicit adapter obligation.
5. Run direct warning-as-error Lean gates for the generated kernel,
   `OperationalTypes`, independent reference, vectors, admission witnesses,
   and operational refinement.
6. Verify the independent mutation vectors execute total fixture adapters via
   `settleHalt`/`runFuel` and observe changed refund, return-data, world, and
   cleanup outputs; execute both dropped and duplicated route evidence through
   the same driver, including malformed machine/frame control routes. A route/hash-only test is
   insufficient. The concrete witness seed must discharge these observations
   by evaluation, not by caller-supplied before/after premises. Record the
   fuel-adequacy obligation as open.

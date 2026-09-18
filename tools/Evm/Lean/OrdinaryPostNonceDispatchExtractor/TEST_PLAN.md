# Test plan

The package test project covers:

- deterministic two-run source extraction and checked-in artifact drift;
- strict source/dependency closure, a production metadata/reference closure for
  the isolated source fixture, and unique temporary output directories;
- paired source-plus-IR mutations for restore/effective-commit option formulas,
  gas-condition/result classification, candidate, lookup, delegation,
  precommit, gas rejection, handoff, member, receiver, and order changes;
- coherent preload mutation rejection (the code handle and delegation cannot
  be modeled as independent nullable fields);
- typed lookup-request and commit-request checks (including the selected
  tracer and `commitRoots:false`) plus the explicit intrinsic-gas rejection
  code;
- semantic target-symbol, receiver, containing-member, CFG-block, and
  source-ordinal presence;
- emitter rejection of unlowered or tampered IR nodes;
- typed source-expression, conditional-arm, adapter-domain, branch-observable,
  binding, and result-classification mutations that fail closed before Lean
  emission;
- compiler-adapter shape mutations, including a baseline extraction that must
  succeed before the mutation is applied;
- theorem-free generated Lean and exact terminal/handoff vocabulary.

Every source-shape mutation first extracts the unchanged fixture and asserts
the expected source/branch/effect counts and output files. It then applies one
mutation and checks both the `ExtractionException` message and the absence of
partial output. This keeps a missing compiler/reference setup from being
mistaken for a fail-closed mutation result.

`Verify.ps1` is the integration gate. It builds the extractor with warnings as
errors, runs the NUnit project, regenerates into a unique temporary directory,
compares SHA-256 identities, runs the Lake package build and each direct Lean
target with warnings as errors, and scans every Lean file for proof-placeholder
tokens.

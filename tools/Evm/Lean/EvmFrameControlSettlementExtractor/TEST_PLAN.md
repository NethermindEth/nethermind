# Stage D test plan

The focused C# suite is deterministic and mutation-oriented. It verifies:

* exact source/dependency hashes, canonical paths, Roslyn member selection,
  duplicate selectors, malformed hashes, and unknown roles;
* staged admission mutation behavior: `BuildForTest` reads the local admission
  so parser/member tests reach their intended checks, while real extraction
  separately verifies the embedded admission identity;
* byte-identical IR, manifest, and Lean output across independent extractions;
* complete expected dispatch-order equality plus its emitted list and actual
  `driveIteration` control-flow ordering;
* every branch descriptor's invocation, effects, and settlement being emitted
  as a reviewed branch contract, rather than only its name;
* absence of a generated theorem, `runFuel`, handwritten-reference import, and
  outer transaction processing calls;
* shared `CanonicalLeaves` control agreement, a separately stated
  `ProductionLeafSimulation`, and `AdmittedStep`-derived settlement/cleanup
  legality rather than universal actual/model congruence;
* fresh/continuation, bytecode/full-precompile, nested CREATE deposit, direct
  opcode event, full-precompile outcome-domain, cleanup, scoped bounds, and
  cancellation distinctions; and
* constructive Lean admission witnesses for bytecode terminal, continuation/
  suspend/direct-inline, and full-frame-precompile states, including a complete
  nonproduction premise instantiation and three one-iteration theorem uses; and
* an explicitly open Stage C-to-Stage D composition obligation, separate
  component theorem uses, preserved status/substate-error observations and
  absence of an arbitrary settlement projector or claimed bridge refinement;
* twelve constructive top/nested Stage C admission/execution vectors across
  success, returned/managed failure, overflow/out-of-gas and missing native
  support, plus a process-unavailable check; and
* actual Lean mutation rejection for erased substate error, missing native
  support changed to success, an out-of-gas boundary moved onto exact gas, and
  an incomplete harness result changed to completed. A combined unmodified
  module must compile before any rejection counts as evidence.

`Verify.ps1` builds warning-as-error, runs the focused suite, performs two
scratch extractions and checked-in-artifact drift comparison, then runs Lake
and direct Lean checks for generated, types, reference, witnesses, the control
refinement, and the non-generated open-composition/vector modules. Both this
gate and the root gate then run `Verify-BridgeMutationGates.ps1`. No vector or
mutation gate discharges the missing concrete Stage C/Stage D adapter.

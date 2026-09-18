# Stage C validation plan

- Build extractor and Microsoft.Testing.Platform suite with warnings as errors; verify nonzero discovered/executed tests.
- Mutate each production or dependency pin independently, including every one of the 18 oracle identities, the Stage A triple, theorem files and the settlement target. Require fail-closed extraction.
- Exercise explicit semantic mutations: swapped action/touch address, touch/pricing order, RIPEMD latch polarity, local-gas installation on pricing failure, returned-failure exception classification, top/nested managed routes, duplicate gas clear, native catch/exit, refund/commit/repayment order, outer catch, operation/action trace order and top action-end/substate order.
- Reject source statement insertion, reordering, unknown admission keys, wrong path casing and output artifact tampering.
- Validate both emitted JSON documents against strict checked-in schemas with exact route/source/member/dependency/oracle/branch/assumption/exclusion counts; compare exact typed branch effect order, all oracle identities and the entire accepted Stage A source closure.
- Emit twice into separate directories, compare bytes and hashes, and validate the checked-in triple against regeneration.
- Compile generated Lean, independently structured reference and universal refinement with warnings as errors; reject theorem/axiom/reference imports in generated output and reject replacement of pre-execution `Entry.Admitted` with a caller-supplied post-execution domain proof.
- Inspect the final diff for edits outside this package. No production bug is presumed from a model discrepancy.

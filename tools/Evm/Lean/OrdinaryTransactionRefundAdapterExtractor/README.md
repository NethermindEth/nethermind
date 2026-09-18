# Ordinary transaction refund adapter

Independently accepted conditional source-audited refund-helper refinement. See [DESIGN.md](DESIGN.md) for the exact helper-entry contract and exclusions. Generated artifacts, checked adapter refinement, the full package gate and independent executable review pass; this does not establish complete transaction execution.

The implementation retains separate normal-settlement and exceptional-halt paths, derives pre-refund gas and code-insertion refund internally, observes all five gas-policy fields, and distinguishes local copies from caller-visible `ref` updates. Primitive transaction/spec/substate and gas snapshots remain upstream observations, not a completed settlement result.

Current work includes the raw fixed-width oracle, selected-source/compiler admission, typed source-expression lowering, theorem-free emission template, atomic manifest-last writer, independent specification and vectors. The source roster is 157 files: 11 semantic trees, 132 compiler-support trees, three real compiler-generated global/assembly-attribute files, and eleven build-selection/projection inputs. The separate reviewed token/directive roster covers all 146 compiled trees. Every audit rederives the actual non-ZK Release MSBuild Compile/ReferencePath/property selection; its 226 selected references include build-only InlineIL and exclude the source assembly's own metadata. Support bodies are not verified by compilation alone.

The schema-1 / extractor-1.0.0 IR and manifest contain the exact 52 dependency paths, 157 source identities, 226 reference identities and 50 expression roles; artifact filenames are exact. The complete package gate passed all 104 C# cases with no failures or skips, both schema baselines and 720 schema negatives, eight compile-first finite Lean semantic mutations, live-source rederivation, byte-identical deterministic triplets and the frozen complete descendant axiom audit (`D:/tmp/formal-verify/refund-acceptance-full-gate.log`). The C# suite includes 28 compile-first source mutations, eight coordinated IR/manifest controls and three interrupted manifest-last triplets.

The independent specification and vectors contain 43 general theorems and 72 named vector/mutation theorems. The complete Lean package passed warning-as-error with 20 jobs (`D:/tmp/formal-verify/refund-full-lean-final.log`). The exact generated/specification refinement requires only raw machine-domain bounds; natural refund, halt-floor, payment and normal success/REVERT settlement corollaries state their additional no-wrap premises separately. Source attachment uses frozen source/IR identities and raw observations, never an assumed completed output. All five tagged entries have inhabited adapter witnesses.

The frozen descendant inventory contains 1,005 exports: 640 `Eip803x`, 91 generated, 164 specification, 38 refinement and 72 vectors. Its SHA-256 is `b6cf921d99722cc15ddcac3f54b32ae07c9035b18692d96091fa266763805396`; both the independent census and final complete audit found only standard axioms. Inventory tampering, malformed/missing/duplicate results, omitted exports, and nested unexpected/forbidden exports are negative controls. Package/test .NET builds are warning-as-error clean, and all four direct Lean targets pass warning-as-error.

The shared `Evm.slnx` Release warning-as-error build passed with zero warnings/errors (`D:/tmp/formal-verify/finite-evm-build1.log`), followed by a fresh refund live-source check (`D:/tmp/formal-verify/refund-post-finite-solution-check.log`). The subsequent scope-warning-only CLI edit was deterministically regenerated and reran the complete package gate; the final post-documentation live-source check is recorded in `D:/tmp/formal-verify/refund-acceptance-postcheck.log`.

The semantic mutation harness first checks the full unmodified universal refinement and independent vectors. It then checks machine-valid raw concrete witnesses against the generated and independent results. Each mutated generated module must compile; its designated witness must fail specifically because `decide` proves the computed equality false. Typing, import, parser and resource failures do not count as detection. These are finite semantic mutation controls, not claims that every mutant completed universal-proof re-elaboration.

## Upstream axiom repair

The seven pre-existing `native_decide` vector exports were independently repaired without changing their statements: five now use `decide` and two use `rfl`. All seven have empty axiom sets, and their three direct targets pass warning-as-error. `UPSTREAM_PINS.json` pins the accepted 22-file dependency set, including the repaired vector hashes. The full refund audit must still include every loaded `Eip803x` descendant export; no nonstandard axiom is excluded or whitelisted.

No production defect was identified. Builds and proof execution remain serialized between extractor gates and independent refinement. The global manifest records this accepted conditional helper-entry scope, and the root gate invokes the complete nonrecursive package gate. The full client claim remains incomplete; downstream drafts are not promoted.

## Accepted artifacts

| Artifact | SHA-256 |
| --- | --- |
| IR | `53f6a24671a917e2675a1a45eb1b49e812b91283fc1c78bf49014f2ad08a1666` |
| Manifest | `527ee805b9be85e57898863fd618fa1ede84862a0cd78ef02083c37ed88278bb` |
| Generated Lean | `b7b21ec284183b073ff1a3f603471b156c07624de3270bc63189b7f7d6600f23` |

Regeneration and full verification used `SOURCE_DATE_EPOCH=1789035784` and `MSBUILDDISABLENODEREUSE=1`. `--check` rederives the live actual compiler/source admission and all emitted bytes. This README and DESIGN are descriptive, not hashed artifact dependencies; executable gates, schemas, proofs, tests, inventories and upstream identities are in the exact dependency closure.

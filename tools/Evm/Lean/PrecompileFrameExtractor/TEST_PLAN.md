# Precompile frame extractor — Stages A and B test plan

The package test project must run these checks without invoking production
execution:

- exact 18-entry provider/address table and all ReleaseSpec activation gates;
- the pinned manifest's 122-source/154-member closure, including the exact
  `EvmPooledMemory.TrySave` overload;
- Amsterdam timestamp selection, standard-vs-ZK source selection, and inactive
  ordinary-code fallback;
- low-number `0x100` array lookup followed by dictionary fallback;
- precompile account-read/access facts and no-delegation resolution;
- cache key address/input/spec identity, input normalization, original-input
  execution, `SupportsCaching`, hit/miss behavior, and invalid-length
  non-caching;
- full-frame versus direct-STATICCALL guards and outcome-dependent ordered
  effects, including conditional action tracing and no direct snapshot restore;
  direct success/failure metadata must match the inline path and contain no
  full-frame snapshot, commit, or return-handler effects;
  the direct `TrySave` output-copy-OOG branch is a separate typed result with
  pricing, leaf run, account touch, child refund, and returndata effects, but
  no output write or success stack push;
  the no-output-write claim must be backed by exact Roslyn admission of
  `EvmPooledMemory.TrySave` (the bounds-failure false return precedes
  `UpdateSize`, `SaveAfterGas`, and all memory mutation);
  full-frame pricing hard failure, returned leaf hard failure, managed nested
  soft revert, and success are separate cases with their production order;
- pricing overflow/ordinary OOG and no-Run behavior;
- returned failure versus managed exception classification, including managed
  nested returndata preservation;
- delegated-precompile suppression, CALL-family target selection, and the
  separate pre-dispatch/boundary cancellation polls (boundary eligibility alone
  is not cancellation, successful completion is required by the canonical
  boundary predicate, and both observations require the cancelable route); the
  exact Roslyn Boolean tree, operator polarities, boundary `break`, and
  immediate boundary-poll composition are mutation-sensitive;
- the complete Amsterdam ancestry and EIP-196/197/198/152/4844/2537/7951
  assignments;
- literal per-leaf identity/address bindings plus exact production-to-oracle
  helper/member-access bindings;
- all 26 production mutation descriptors, including strict field/null/case,
  omission/duplicate, source/member/dependency/artifact digest, canonical
  newline, and checked-artifact identity mutations;
- deterministic two-run IR, Lean, and manifest bytes.

These tests establish extraction shape and classification only. They must not
claim cryptographic/native correctness or WorldJournal composition.

Stage B additionally requires:

- byte-identical deterministic regeneration of its IR, manifest, and generated
  Lean kernel;
- strict schema validation of all Stage B artifacts;
- exact reuse of the accepted Stage A 122-source/154-member/23-dependency and
  18-route identity;
- ten concrete operational sources, 24 exact members, nine direct operational
  branches, 18 exact typed oracle bindings, and 28 Stage B mutation descriptors;
- exact source/member admission for direct eligibility, input load, child gas,
  pricing, leaf invocation, failure clearing/restoration, success touch/refund,
  returndata, output `TrySave`, stack result, and both cancellation polls;
- output-copy OOG after leaf success, account touch, child refund, and returndata
  installation, with no memory write or success stack push;
- hash/type/definition/theorem identity checks for the independent reference and
  universal refinement source, including the reference-only decision/render
  architecture;
- `UInt8` byte domains and the admitted post-pop stack/memory/resolver facts at
  the typed inline-helper boundary;
- mutation rejection for source order/polarity, cancellation, `TrySave`, proof
  identities, every child-gas field, concrete pricing/refund/halt restoration,
  and each generated artifact;
- standalone warning-as-error compilation of the generated kernel, independent
  reference, and universal refinement theorem.

The Stage B theorem quantifies over every typed input and leaf oracle. It proves
only the admitted standard direct-precompile inline-helper and dispatch-boundary
transition. The outer `RunByteCode` exceptional-halt handling and final frame
residue, oracle bodies, concrete world journal, surrounding CALL/transaction/block
reachability, runtime representation, and whole-EVM composition remain open.

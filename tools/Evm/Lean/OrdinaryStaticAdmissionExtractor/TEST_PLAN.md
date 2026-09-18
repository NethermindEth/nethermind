# Verification matrix

The standalone C# suite currently contains 96 discovered cases. It covers:

- deterministic regeneration, checked-in artifact equality, check mode, and structural JSON reserialization;
- all 12 branch identities, source-lowered guard depths, synthetic legacy fallthrough, and bridge field order;
- simultaneous-failure ordering, `<` equality neighbors, SetCode creation/authentication predicates, and the exact
  SkipValidation scope;
- EIP-8037 cap, standard/floor/minimum ordering, legacy parallel allowance, signed casts, and UInt64 subtraction;
- source mutations for every admitted Boolean guard, ordered return AST, numeric widths and enum layouts, EIP-7825
  constant identity, SetCode error/success mappings, bridge argument order, initializer result-field order,
  initializer arithmetic, and static error/EVM-result mapping;
- semantic IR operator tampering and source/manifest identity tampering.

`Reference/OrdinaryStaticAdmissionVectors.lean` independently executes branch-order, equality, parallel, fixed-width
subtraction, and signed-state vectors with `native_decide`. The canonical EIP-8037 initializer vectors remain the
independent source for initializer wrap/failure/cap boundaries; the standalone `Verify.ps1` builds both vector sets.

The C# suite intentionally mutates temporary source copies only. It never edits production source, checked-in
artifacts, parent solutions, or the repository verifier.

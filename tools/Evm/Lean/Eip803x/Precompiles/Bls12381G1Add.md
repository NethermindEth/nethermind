# BLS12-381 G1 addition precompile leaf reference

`Bls12381G1Add.lean` is an independently reviewed handwritten executable reference for
the standard-mainnet EIP-2537 `BLS12_G1ADD` precompile at address `0x0b`. It
models exact 256-byte framing, two length-indexed 128-byte point encodings,
left-to-right validation, both infinity shortcuts, configurable fixed gas,
exact output length, name, address, and default caching metadata.

The specification is pinned to
[`ethereum/EIPs@1dd2558f.../EIPS/eip-2537.md`](https://github.com/ethereum/EIPs/blob/1dd2558f9a68d9453aed71c803fdda09d83c6e37/EIPS/eip-2537.md).
The production sources compared while writing the reference are
[`Bls12381G1AddPrecompile.cs`](../../../../../src/Nethermind/Nethermind.Evm.Precompiles/Bls12381G1AddPrecompile.cs)
and
[`std/Bls12381G1AddPrecompile.cs`](../../../../../src/Nethermind/Nethermind.Evm.Precompiles/std/Bls12381G1AddPrecompile.cs).

`Bls12381G1AddVectors.lean` carries nine cases: empty, one-byte-short,
one-byte-long, infinity with infinity and either finite operand, `(0,2)`
doubled, and invalid left/right points. The `(0,2)` doubling result follows
independently from the BLS12-381 equation: its tangent has slope zero, so the
result is `(0,p-2)`. It is duplicated in separate oracle and expected-output tables.
Twelve mutation sentinels cover metadata, gas, framing, both validation
gates, both infinity projections, nontrivial addition, and corruption on
either side of the known-answer relation. The targeted six-job build passes
13 model theorems, five vector theorems, and 16 examples.

Independent review compared the model with the pinned EIP and both standard
production partials, checked the curve-equation derivation above, reran the
targeted six-job Lean build, and ran the focused production suite with
118/118 cases passing.

This leaf deliberately does not verify 64-byte field decoding, the BLS12-381
curve equation, standard-library point representation, nontrivial addition,
the native or zkEVM implementation, production source extraction,
registration, metrics, allocation, caching behavior, the generic precompile
wrapper, frame effects, or transaction/block composition.

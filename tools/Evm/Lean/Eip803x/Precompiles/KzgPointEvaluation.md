# KZG point-evaluation precompile leaf reference

`KzgPointEvaluation.lean` is an independently reviewed handwritten executable reference
for the standard-mainnet EIP-4844 precompile at address `0x0a`. It models the
exact 192-byte input grammar, 32/32/32/48/48-byte field slicing, commitment
hash comparison before proof verification, fixed configurable gas, exact
64-byte success output, failures, name, address, and default caching metadata.

The specification is pinned to
[`ethereum/EIPs@70471d02.../EIPS/eip-4844.md`](https://github.com/ethereum/EIPs/blob/70471d02d48a81ca963407abe9c48706059dc8e8/EIPS/eip-4844.md).
The production sources compared while writing the reference are
[`KzgPointEvaluationPrecompile.cs`](../../../../../src/Nethermind/Nethermind.Evm.Precompiles/KzgPointEvaluationPrecompile.cs),
[`std/KzgPointEvaluationPrecompile.cs`](../../../../../src/Nethermind/Nethermind.Evm.Precompiles/std/KzgPointEvaluationPrecompile.cs),
and
[`zkevm/KzgPointEvaluationPrecompile.cs`](../../../../../src/Nethermind/Nethermind.Evm.Precompiles/zkevm/KzgPointEvaluationPrecompile.cs).

`KzgPointEvaluationVectors.lean` carries eight cases: empty, one-byte-short,
one-byte-long, two accepted identity-commitment evaluations, a versioned-hash
mismatch, an invalid proof, and commitment-hash failure. The expected input,
success-output, and oracle hash literals are separate. Eleven mutation
sentinels cover metadata, gas, framing, field offsets, both verification gates,
the success output, and oracle-table corruption. The targeted six-job build
passes 12 model theorems, five vector theorems, and 18 examples; the success
literal is also decoded to 4096 and the exact BLS modulus. Independent review
recomputed the versioned hash, checked the exact output and both successful
fixtures, and passed all 32 focused production cases.

This leaf deliberately does not verify SHA-256, BLS12-381 decoding, the KZG
proof primitive or trusted setup, native library initialization, production
source extraction, registration, metrics, allocation, caching behavior, the
generic precompile wrapper, frame effects, or transaction/block composition.
Independent review accepts only this oracle-bounded handwritten leaf; none of
those excluded cryptographic, production, or compositional obligations are
discharged by that acceptance.

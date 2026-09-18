# BLS12-381 G2 addition precompile leaf reference

`Bls12381G2Add.lean` is an independently reviewed handwritten executable reference for
the standard-mainnet EIP-2537 `BLS12_G2ADD` precompile at address `0x0d`.
It models exact 512-byte framing, two length-indexed 256-byte point
encodings, left-to-right validation, both infinity projections, configurable
fixed gas, exact output length, name, address, and default caching metadata.

The specification is pinned to
[`ethereum/EIPs@1dd2558f...`](https://github.com/ethereum/EIPs/blob/1dd2558f9a68d9453aed71c803fdda09d83c6e37/EIPS/eip-2537.md).
That revision specifies G2 points as 256-byte concatenations of 128-byte Fp2
coordinates, uses 512-byte concatenation for two inputs, encodes infinity as
256 zero bytes, and prices G2 addition at 600 gas.

The production sources and test assets are read at Nethermind commit
`b2478235e71e6a7ec2a509aa0155e25d5fdfff80`:

* [`Bls12381G2AddPrecompile.cs`](../../../../../src/Nethermind/Nethermind.Evm.Precompiles/Bls12381G2AddPrecompile.cs)
* [`std/Bls12381G2AddPrecompile.cs`](../../../../../src/Nethermind/Nethermind.Evm.Precompiles/std/Bls12381G2AddPrecompile.cs)
* [`Bls12381G2AddPrecompileTests.cs`](../../../../../src/Nethermind/Nethermind.Evm.Test/Bls12381G2AddPrecompileTests.cs)
* [`add_G2_bls.json`](../../../../../src/Nethermind/Nethermind.Evm.Test/PrecompileVectors/Bls/add_G2_bls.json)
* [`fail-add_G2_bls.json`](../../../../../src/Nethermind/Nethermind.Evm.Test/PrecompileVectors/Bls/fail-add_G2_bls.json)

`Bls12381G2AddVectors.lean` carries nine cases: empty, 511-byte, and
513-byte inputs; infinity with infinity and either generator projection;
generator plus generator; and invalid left/right points. The generator and
the checked-in `bls_g2add_(g2+g2=2*g2)` result use separate oracle and
expected-output tables. A small fail-closed hexadecimal parser returns no
bytes for malformed or odd text, and exact 256-byte proofs are required at
each fixture boundary. The known answer was independently compared with the
checked-in JSON entry outside Lean.

The targeted build passes 13 model theorems, 6 vector theorems, 23 examples,
9 boundary vectors, and 14 mutation sentinels. The mutations cover metadata,
fixed gas, right-padding and truncation framing, both validation gates, both
infinity projections, addition, and one-byte corruption of both known-answer
tables.

Independent review compared the model with the pinned EIP, the standard and
zkEVM partials, and the checked-in success/failure vectors. It confirmed the
ordered validation and absence of a subgroup check, independently matched the
generator fixture and result, reran the targeted six-job Lean build, and ran
the focused production suite with 117/117 cases passing.

This leaf deliberately does not verify Fp/Fp2 decoding and bounds, the
BLS12-381 curve equation, infinity decoding, group addition, or cryptographic
correctness; native or zkEVM behavior; production source extraction,
registration, metrics, allocation, caching implementation; the generic
precompile wrapper; frame effects; or transaction/block composition.

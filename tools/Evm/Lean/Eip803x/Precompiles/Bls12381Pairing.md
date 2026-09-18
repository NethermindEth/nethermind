# BLS12-381 pairing-check candidate

Status: **unreviewed candidate**. This is a standalone handwritten reference,
not production extraction, an equivalence proof, or a cryptographic proof.

Bls12381Pairing.lean models the standard-mainnet EIP-2537
BLS12_PAIRING_CHECK leaf at address 0x0f. It is pinned to
[EIP-2537 at 1dd2558f](https://github.com/ethereum/EIPs/blob/1dd2558f9a68d9453aed71c803fdda09d83c6e37/EIPS/eip-2537.md)
and compares the wrapper surfaces at Nethermind
b2478235e71e6a7ec2a509aa0155e25d5fdfff80:

* [base](../../../../../src/Nethermind/Nethermind.Evm.Precompiles/Bls12381PairingCheckPrecompile.cs)
* [standard](../../../../../src/Nethermind/Nethermind.Evm.Precompiles/std/Bls12381PairingCheckPrecompile.cs)
* [zkEVM](../../../../../src/Nethermind/Nethermind.Evm.Precompiles/zkevm/Bls12381PairingCheckPrecompile.cs)
* [focused tests](../../../../../src/Nethermind/Nethermind.Evm.Test/Bls12381PairingCheckPrecompileTests.cs)

The current base, standard, zkEVM, EIP helper, focused test, and both fixture
assets match that pinned ancestor.

The candidate owns address, BLS12_PAIRING_CHECK name, caching metadata,
identity normalization, nonempty exact multiples of 384 bytes, and pricing:
37700 + 32600 * floor(inputLength / 384). Every admitted pair has a
128-byte G1 encoding followed by a 256-byte G2 encoding. It decodes every raw
pair, validates G1 and G2 field, curve, and subgroup observations, then drops
a pair if either raw point is infinity. Thus an invalid point paired with raw
infinity is still rejected; only a fully valid all-infinity input compacts to
the empty product and returns true. Successful calls always return exactly 32
bytes: 31 zero bytes followed by 00 or 01.

The reference has a deliberately single invalid-point result. Its validation
fold gives the handwritten model a canonical order, but it does not claim the
production parallel implementation selects errors in that order or has a
particular scheduling/call order. In particular, it makes no parallel-order
claim.

## Oracle boundary

PairingOracle explicitly supplies G1/G2 field, curve, and subgroup checks,
the Miller-loop product, final-exponentiation Boolean, and separate standard
native and zkEVM backend observations. No theorem equates either backend with
the reference. The model consequently does not establish BLS12-381 arithmetic,
Fp/Fp2 decoding, curve or subgroup correctness, raw-infinity decoding,
Miller/final-exponentiation correctness, native or accelerator behavior,
allocation, exceptions, concurrency, registration, generic precompile wrapper
gas/OOG behavior, or transaction/block composition.

## Fixture evidence

Bls12381PairingVectors.lean uses the existing fail-closed hexadecimal parser
and carries literal, length-indexed tables for every checked-in success and
failure fixture. Expected-result and oracle-result tables are declared
separately. Each vector independently checks the model result against its
oracle result and then checks that oracle result against the expected result;
the all-infinity true and interleaved false focused cases follow the same two
links. Their source assets are:

| Asset | Entries | SHA-256 |
| --- | ---: | --- |
| [pairing_check_bls.json](../../../../../src/Nethermind/Nethermind.Evm.Test/PrecompileVectors/Bls/pairing_check_bls.json) | 15 | 01c49b33eef4c825a4e38f6bbcd00d88d723874dcee9990015ba5d1797ae43b3 |
| [fail-pairing_check_bls.json](../../../../../src/Nethermind/Nethermind.Evm.Test/PrecompileVectors/Bls/fail-pairing_check_bls.json) | 9 | 35aefbc5feb93d599e72922f32f0c5318fdfd099a95729af2d6ab0663b0de5bc |

The 31 executable vectors include all 24 checked-in cases, a three-pair
all-infinity product, both subgroup-invalid infinity-pair directions both
alone and after a valid pair, a G2 top-byte rejection, and a finite/infinity
compaction case. Literal theorems
check all source lengths, 32-byte result widths, entry counts, floor-gas
boundaries, and agreement of the independent expected/oracle output tables.
Local mutation sentinels cover metadata, 128/256/384 framing, empty admission,
padding, fixed and per-pair gas, floor pricing, all six validation gates,
validation-before-compaction, raw-infinity compaction, empty-product truth,
Boolean byte order, Miller/final-exponentiation observations, expected/oracle
literals, and the two backend boundary fields.

These fixtures use a finite lookup oracle solely to execute the checked-in
table. Its fallback values do not claim pairing results for inputs outside
those vectors. The candidate remains unreviewed even when its direct Lean and
targeted Lake checks pass.

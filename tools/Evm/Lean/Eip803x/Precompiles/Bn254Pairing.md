# BN254 pairing-check precompile leaf reference

`Bn254Pairing.lean` is an independently reviewed handwritten executable reference for the
standard-mainnet EIP-197/EIP-1108 `BN254_PAIRING` precompile at address `0x08`.
It models exact multiple-of-192 input admission, floor pair-count pricing even
for invalid lengths, the valid empty-input result, configurable base/per-pair
gas, exact 32-byte Boolean output encoding, and
address/name/default-caching metadata, including the identity cache key.
Complete point decoding and pairing
semantics are isolated behind an explicit oracle.

The semantic and pricing specifications are pinned to
[`ethereum/EIPs@9e393a79.../EIPS/eip-197.md`](https://github.com/ethereum/EIPs/blob/9e393a79d9937f579acbdcb234a67869259d5a96/EIPS/eip-197.md)
and
[`ethereum/EIPs@8cc38b9d.../EIPS/eip-1108.md`](https://github.com/ethereum/EIPs/blob/8cc38b9d7566132c5a05cef8d3b573d7fc2c44e8/EIPS/eip-1108.md).
The standard-mainnet name follows pinned
[`EIP-7910`](https://github.com/ethereum/EIPs/blob/0c82d532192eca83ab5ce12b2a0d3e019c803066/EIPS/eip-7910.md).
The production sources compared while writing the reference are
[`BN254PairingCheckPrecompile.cs`](../../../../../src/Nethermind/Nethermind.Evm.Precompiles/BN254PairingCheckPrecompile.cs),
[`std/BN254PairingCheckPrecompile.cs`](../../../../../src/Nethermind/Nethermind.Evm.Precompiles/std/BN254PairingCheckPrecompile.cs),
[`zkevm/BN254PairingCheckPrecompile.cs`](../../../../../src/Nethermind/Nethermind.Evm.Precompiles/zkevm/BN254PairingCheckPrecompile.cs),
and
[`BN254.cs`](../../../../../src/Nethermind/Nethermind.Evm.Precompiles/BN254.cs).

`Bn254PairingVectors.lean` carries eight cases for empty input, one and two
infinity pairs, a current production one-pair non-identity fixture, a trailing
infinity identity, invalid lengths below and above one pair, and oracle
failure. Eleven mutation sentinels cover metadata, pre-EIP-1108 gas constants,
floor versus ceiling pricing, length admission, empty-input truth, Boolean
endianness, and corruption of either side of the known answer.
The targeted six-job build passes 14 model theorems, five vector theorems,
and 18 examples. Independent review also confirmed the pinned sources, the
production base and standard partials, the native boundary, all 51 focused
production cases, and the exact `OnePairNotOne` known-false fixture.

This leaf deliberately does not verify Fp/Fp2 decoding and bounds, G1/G2 curve
and subgroup membership, infinity decoding, Miller loops, chunked
accumulation, final exponentiation, native or zkEVM behavior, production source
extraction, registration, metrics, allocation, caching implementation, the
generic precompile wrapper, frame effects, or transaction/block composition.
Independent review accepts only this oracle-bounded handwritten leaf; none of
the excluded production or cryptographic obligations are discharged by that
acceptance.

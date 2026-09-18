# BN254 addition precompile leaf reference

`Bn254Add.lean` is an independently reviewed handwritten executable reference for the
standard-mainnet EIP-196/EIP-1108 `BN254_ADD` precompile at address `0x06`.
It models right-zero-padding of short inputs, truncation at 128 bytes, two
length-indexed 64-byte point encodings, left-to-right validation, configurable
fixed gas, exact output length, and address/name/default-caching metadata. It
also records production's cache-key normalization: clamp to 128 bytes, then
remove trailing zero bytes.

The semantic and pricing specifications are pinned to
[`ethereum/EIPs@9e393a79.../EIPS/eip-196.md`](https://github.com/ethereum/EIPs/blob/9e393a79d9937f579acbdcb234a67869259d5a96/EIPS/eip-196.md)
and
[`ethereum/EIPs@8cc38b9d.../EIPS/eip-1108.md`](https://github.com/ethereum/EIPs/blob/8cc38b9d7566132c5a05cef8d3b573d7fc2c44e8/EIPS/eip-1108.md).
The standard-mainnet name follows pinned
[`EIP-7910`](https://github.com/ethereum/EIPs/blob/0c82d532192eca83ab5ce12b2a0d3e019c803066/EIPS/eip-7910.md).
The production sources compared while writing the reference are
[`BN254AddPrecompile.cs`](../../../../../src/Nethermind/Nethermind.Evm.Precompiles/BN254AddPrecompile.cs)
and
[`std/BN254AddPrecompile.cs`](../../../../../src/Nethermind/Nethermind.Evm.Precompiles/std/BN254AddPrecompile.cs).

`Bn254AddVectors.lean` carries eight cases for empty input, a single 64-byte
point, exact and oversized known answers, invalid left/right points, and both
infinity identities. The first current Nethermind test literal is duplicated
in separate oracle and expected-output tables. Eleven mutation sentinels cover
metadata, the EIP-1108 150-gas price, padding direction, truncation, cache-key
clamping, both validation gates, and corruption on either side of the known
answer. The targeted six-job build passes 12 model theorems, five vector
theorems, and 16 examples.

Independent review compared the model with pinned EIP-196, EIP-1108, and
EIP-7910 plus both standard production partials, and matched the first
production test input/result byte for byte. The targeted six-job Lean build
passes, and the focused production suite passes 19/19 cases.

This leaf deliberately does not verify base-field decoding and bounds, the
BN254 curve equation, infinity decoding, group addition, native or zkEVM
behavior, production source extraction, registration, metrics, allocation,
caching implementation, the generic precompile wrapper, frame effects, or
transaction/block composition.

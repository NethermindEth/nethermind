# Amsterdam MODEXP precompile leaf reference

`ModExp.lean` is an independently reviewed handwritten executable reference for the
standard-mainnet EIP-198 `MODEXP` precompile at address `0x05` under the pinned
Amsterdam EIP-2565/EIP-7823/EIP-7883 configuration. It models three
right-zero-padded 32-byte length words, production's `uint32` overflow
saturation, right-zero-padded operands, the 1,024-byte per-field cap, the
zero-base/zero-modulus short circuit, zero-modulus output, configurable
Amsterdam gas arithmetic with `uint64` saturation, exact result width, and
production cache-key truncation. Modular exponentiation itself is an explicit
oracle.

The semantic and pricing specifications are pinned to
[`EIP-198`](https://github.com/ethereum/EIPs/blob/9e393a79d9937f579acbdcb234a67869259d5a96/EIPS/eip-198.md),
[`EIP-2565`](https://github.com/ethereum/EIPs/blob/9e393a79d9937f579acbdcb234a67869259d5a96/EIPS/eip-2565.md),
[`EIP-7823`](https://github.com/ethereum/EIPs/blob/b55cdb0ee78a696327cf76d1c5cf8088d73499ca/EIPS/eip-7823.md),
and
[`EIP-7883`](https://github.com/ethereum/EIPs/blob/b55cdb0ee78a696327cf76d1c5cf8088d73499ca/EIPS/eip-7883.md).
The EIP-7910 name is pinned at
[`0c82d532...`](https://github.com/ethereum/EIPs/blob/0c82d532192eca83ab5ce12b2a0d3e019c803066/EIPS/eip-7910.md).
The production sources compared while writing the reference are
[`ModExpPrecompile.cs`](../../../../../src/Nethermind/Nethermind.Evm.Precompiles/ModExpPrecompile.cs),
[`std/ModExpPrecompile.cs`](../../../../../src/Nethermind/Nethermind.Evm.Precompiles/std/ModExpPrecompile.cs),
and
[`zkevm/ModExpPrecompile.cs`](../../../../../src/Nethermind/Nethermind.Evm.Precompiles/zkevm/ModExpPrecompile.cs).

`ModExpVectors.lean` carries thirteen cases for empty/short-circuit input, exact
and widened known answers, cache suffix truncation, zero modulus, the 1,024 and
1,025 boundaries, Amsterdam long-exponent pricing, both saturating header-overflow paths,
oracle failure, and wrong-width oracle output. Fourteen mutation groups cover
metadata, every Amsterdam schedule parameter, saturation and word rounding,
the exponent size cap, cache normalization, and both sides of a known answer.
The targeted six-job build passes eleven model theorems, six vector theorems,
and 29 examples. Independent review confirmed the pinned formulas and their
current production transcription, both `uint32` saturation paths, the bounded
offset premise induced by EIP-7823, and all 259 focused production cases.

This leaf deliberately does not verify GMP or zkEVM big-integer arithmetic,
native bindings, allocation/disposal and exception behavior, pre-EIP-2565 or
pre-EIP-7883 schedules, production source extraction, registration, metrics,
caching implementation, the generic precompile wrapper, frame effects, or
transaction/block composition. Independent review accepts only this
oracle-bounded handwritten Amsterdam leaf; none of those excluded production,
native, or compositional obligations are discharged by that acceptance.

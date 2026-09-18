# BLAKE2F precompile leaf reference

`Blake2F.lean` is an independently reviewed handwritten executable reference for the
standard-mainnet EIP-152 precompile at address `0x09`. It models the exact
213-byte input grammar, big-endian 32-bit round count, final-block flags `0`
and `1`, invalid-length and invalid-flag results, input normalization, one gas
per round, exact 64-byte output shape, name, address, and caching metadata.

The specification is pinned to
[`ethereum/EIPs@5510973b.../EIPS/eip-152.md`](https://github.com/ethereum/EIPs/blob/5510973b40973b6aa774f04c9caba823c8ff8460/EIPS/eip-152.md).
The production sources compared while writing the reference are
[`Blake2FPrecompile.cs`](../../../../../src/Nethermind/Nethermind.Evm.Precompiles/Blake2FPrecompile.cs)
and
[`std/Blake2FPrecompile.cs`](../../../../../src/Nethermind/Nethermind.Evm.Precompiles/std/Blake2FPrecompile.cs).

`Blake2FVectors.lean` carries eight literal boundary cases: empty, one byte
short, one byte long, invalid flag, zero rounds, 12 rounds with each valid
flag, and maximum 32-bit rounds. Three output fixtures are transcribed from
the pinned EIP into separate oracle and expected-output tables. Four vector
theorems and 18 examples exercise those eight cases. Nine mutation sentinels
cover byte order, accepted flags, malformed-input pricing, input length,
metadata, caching, output length, and a one-byte known-answer corruption.

Independent review compared the model with the pinned EIP and the standard
production partials, confirmed the production-matching validation and pricing
order, and reran the targeted six-job Lean build. The eight vectors, four
vector theorems, 14 model theorems, 18 examples, and nine mutation sentinels
pass.

This leaf deliberately does not verify the BLAKE2b compression primitive,
the scalar/SSE4.1/AVX2 implementations, runtime instruction selection,
allocation and CLR behavior, production source extraction, registration,
the generic precompile wrapper, frame effects, or transaction/block
composition. The maximum-round vector uses the explicit compression oracle's
fallback output only to exercise the 32-bit pricing boundary; it is not a
cryptographic known-answer claim.

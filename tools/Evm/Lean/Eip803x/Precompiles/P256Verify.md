# P256VERIFY precompile leaf reference

`P256Verify.lean` is an independently reviewed handwritten executable reference for the
standard-mainnet EIP-7951 precompile at address `0x100`. It models exact
160-byte framing, five length-indexed 32-byte words, strict signature-scalar
and public-key field bounds, curve/infinity/verification gates, fixed
configurable gas, always-successful EVM status, the exact empty-or-32-byte
output contract, and metadata.

The specification and its test asset are pinned to
[`ethereum/EIPs@b55cdb0e.../EIPS/eip-7951.md`](https://github.com/ethereum/EIPs/blob/b55cdb0ee78a696327cf76d1c5cf8088d73499ca/EIPS/eip-7951.md)
and
[`assets/eip-7951/test-vectors.json`](https://github.com/ethereum/EIPs/blob/b55cdb0ee78a696327cf76d1c5cf8088d73499ca/assets/eip-7951/test-vectors.json).
The production sources compared while writing the reference are
[`SecP256r1Precompile.cs`](../../../../../src/Nethermind/Nethermind.Evm.Precompiles/SecP256r1Precompile.cs)
and
[`std/SecP256r1Precompile.cs`](../../../../../src/Nethermind/Nethermind.Evm.Precompiles/std/SecP256r1Precompile.cs).

`P256VerifyVectors.lean` carries 13 cases for empty, one-byte-short,
one-byte-long, the first pinned EIP known answer, zero/order scalar bounds,
field-modulus bounds, infinity, off-curve, and unsuccessful verification.
Thirteen mutation sentinels constrain metadata, the Amsterdam 6,900-gas
price, exact framing, strict scalar and field inequalities, curve and
infinity checks, output bytes, and successful failure status. The targeted
six-job build passes 17 model theorems, two vector theorems, and 27 examples.

The EIP asset still records the superseded RIP-7212 price of 3,450 gas, so the
reference deliberately takes the final EIP-7951 value of 6,900 from the pinned
normative text and treats 3,450 as a detected pricing mutation. The known
answer is recognized by the explicit oracle and therefore checks framing and
result projection, not P-256 arithmetic independently.

Independent review compared the model against the pinned EIP-7951 text, its
first test-vector asset, both standard production partials, and the production
precompile suite. The targeted six-job Lean build passes, and the focused
production filter passes all 887 discovered cases.

This leaf deliberately does not prove the P-256 curve equation, modular
arithmetic, ECDSA verification, .NET cryptographic implementation, production
source extraction, registration, metrics, allocation, caching behavior, the
generic precompile wrapper, frame effects, or transaction/block composition.

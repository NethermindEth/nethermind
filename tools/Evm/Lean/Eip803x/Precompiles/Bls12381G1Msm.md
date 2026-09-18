# BLS12 G1 MSM precompile reference

`Bls12381G1Msm.lean` is an independently reviewed handwritten reference
for the standard-mainnet Amsterdam EIP-2537 `BLS12_G1MSM` leaf at `0x0c`.
It is not production extraction or a proof of BLS12-381 arithmetic.

The specification is pinned to
[`EIP-2537@1dd2558f`](https://github.com/ethereum/EIPs/blob/1dd2558f9a68d9453aed71c803fdda09d83c6e37/EIPS/eip-2537.md).
The comparison surfaces are the production
[`base`](../../../../../src/Nethermind/Nethermind.Evm.Precompiles/Bls12381G1MsmPrecompile.cs),
[`std`](../../../../../src/Nethermind/Nethermind.Evm.Precompiles/std/Bls12381G1MsmPrecompile.cs),
and [`zkEVM`](../../../../../src/Nethermind/Nethermind.Evm.Precompiles/zkevm/Bls12381G1MsmPrecompile.cs)
implementations plus `Eip2537.cs`, `Eip2537.zkevm.cs`, and the default
`IPrecompile` metadata/normalization contract. These sources have no worktree
delta from the pinned ancestor `b2478235e71e6a7ec2a509aa0155e25d5fdfff80`.

The model enforces nonempty multiples of 160 bytes, partitions each item into
an exactly 128-byte point and 32-byte scalar, and proves complete decoding for
every admitted length. Pricing uses the original floor item count, including
invalid trailing bytes and infinity entries, with the complete 129-entry G1
table (including the zero-count sentinel), 12,000 multiplication gas, integer
division by 1,000, and the 519 cap starting at 128 items. All entries were
cross-checked against both the pinned EIP and production table.

Single-pair execution validates the point and subgroup before zero-scalar or
infinity shortcuts. Multi-pair execution drops raw infinity entries, validates
all remaining points including finite zero-scalar entries, preserves their
order/scalars, then invokes the MSM oracle; an empty compacted list returns
infinity. Scalars retain their full unsigned big-endian 256-bit encoding and
are not bounded by the subgroup order. The standard native scalar reversal is
represented separately, with a byte-preservation theorem.

The canonical left-to-right validation fold is a reference order, not a claim
about production thread scheduling. Standard MSM validation uses `Parallel.For`;
zkEVM checks coordinate encodings before the accelerator performs curve and
subgroup work. All point-validation failures are intentionally collapsed to
one model error. Native error strings, failure-selection order, and validation
call counts are outside the observation. The std/zkEVM observable equivalence
still requires oracle/adapter laws; it is not proved here.

## Fixtures and checks

The vectors reuse the existing fail-closed hexadecimal fixture parser from
`Bls12381G2AddVectors.lean` and the 128-byte point type from `Bls12381G1Add.lean`.
Expected and oracle literals for generator doubling, random multiplication,
and two-point MSM are separately declared and byte-compared to checked-in
`PrecompileVectors/Bls` assets:

| Asset | SHA-256 |
|---|---|
| `mul_G1_bls.json` | `3518be34a3bb4fdf92ed21ee4ed4a083cfd4597b797dbeafa650a6d43b498b3d` |
| `multiexp_G1_bls.json` | `ec061cba8d158901f64ac1084ca7a6f1e2c51d33ca49fecd1ede979ca55dab65` |
| `fail-mul_G1_bls.json` | `07c2114c05cd0f76a2e154789a9c0564a7388e7948dc93108e618d7b3b756d62` |
| `fail-multiexp_G1_bls.json` | `fc01b40a1c869c4ff832deba113a2793975fdca8832b3b31477a19e578d6799f` |

The selected known answers include `bls_g1mul_(g1+g1=2*g1)`,
`bls_g1mul_(1*g1=g1)`, `bls_g1mul_random*g1`, its
`_unnormalized_scalar` counterpart, and `bls_g1multiexp_(2g1+2p1)`.
The invalid field, off-curve, top-byte, and wrong-subgroup point encodings
come from the corresponding failure assets. Zero-scalar invalid-point cases,
repeated/interleaved infinity cases, and the full-uint256 scalar are explicit
adversarial extensions, not additional published known-answer fixtures.
The fixture oracle is a finite test table; its fallback values are not claims
about cryptographic results outside the listed cases.

The targeted nine-job Lean build passes 23 model theorems, six vector theorems,
21 examples, 25 behavioral vectors, eight gas-boundary vectors, and 18 local
mutation sentinels. The latter are explicit alternatives, not a production
source-mutation campaign. An unrelated final review checked the pinned EIP,
standard and zkEVM sources, all 129 discount entries, selected fixture literals,
and the published boundary; the focused production suite passes 244/244 cases.

## Remaining boundary

The typed oracle supplies field/curve validity, subgroup membership, single
multiplication, and MSM results. Correct behavior on infinity, validation of all
finite points even with zero scalars, scalar interpretation, arithmetic, and
canonical output encoding require corresponding cryptographic/adapter laws.
The model proves output length, not validity of an arbitrary oracle's point.
Native BLST and zkEVM accelerators, memory ownership, concurrency, allocation,
exceptions, runtime initialization, machine-width arithmetic, source extraction,
registration/fork reachability, metrics, caching implementation, wrapper gas/OOG
handling, and frame/transaction/block composition remain unproved. Natural-number
pricing is not a uint64 refinement. No new pinned-production defect is claimed.

Independent review accepts this component only within the oracle-bounded scope
above; it does not upgrade any production extraction or whole-EVM claim.

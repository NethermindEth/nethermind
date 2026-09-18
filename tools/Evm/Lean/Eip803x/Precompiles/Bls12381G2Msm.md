# BLS12-381 G2 MSM precompile reference

Status: **independently reviewed and accepted** within the explicit
handwritten/oracle boundary below.

Bls12381G2Msm.lean is a handwritten executable candidate for the
standard-mainnet EIP-2537 BLS12_G2MSM precompile at address 0x0e. It owns the
observable wrapper behavior: a nonempty input whose byte length is a multiple
of 288, a 256-byte G2 point followed by a 32-byte unsigned big-endian scalar
per pair, zero base gas, configurable multiplication gas of 22500 for the
Amsterdam schedule, the complete G2 discount schedule, raw-infinity
compaction for multipair inputs, exact 256-byte results, name, and caching
metadata.

The specification is pinned to
[ethereum/EIPs at 1dd2558f](https://github.com/ethereum/EIPs/blob/1dd2558f9a68d9453aed71c803fdda09d83c6e37/EIPS/eip-2537.md).
That revision defines address 0x0e, 256-byte G2 encodings, 288-byte MSM
items, unrestricted 32-byte scalar inputs, 22500 G2 multiplication gas, and
the floor-pricing rule. Thus malformed framing still has pricing based on
floor(length / 288): 287 is 0, 289 and 575 are 22500, and 577 is 45000.

The candidate transcribes all 129 G2 schedule entries, including its
zero-pair sentinel. It uses the EIP's 524 discount at and beyond a pair count
of 128. The checked boundaries are counts 0, 1, 2, 3, 7, 125, 126, 127, 128,
129, and 524; their exact gas results are 0, 22500, 45000, 62302, 127890,
1479375, 1488375, 1497330, 1509120, 1520910, and 6177960.

The source comparison is pinned to Nethermind
b2478235e71e6a7ec2a509aa0155e25d5fdfff80:

* [base G2 MSM wrapper](../../../../../src/Nethermind/Nethermind.Evm.Precompiles/Bls12381G2MsmPrecompile.cs)
* [standard G2 MSM implementation](../../../../../src/Nethermind/Nethermind.Evm.Precompiles/std/Bls12381G2MsmPrecompile.cs)
* [zkEVM G2 MSM implementation](../../../../../src/Nethermind/Nethermind.Evm.Precompiles/zkevm/Bls12381G2MsmPrecompile.cs)
* [EIP-2537 discount source](../../../../../src/Nethermind/Nethermind.Evm.Precompiles/Eip2537.cs)
* [focused G2 MSM test class](../../../../../src/Nethermind/Nethermind.Evm.Test/Bls12381G2MsmPrecompileTests.cs)

The standard single-pair branch decodes and subgroup-checks before its
zero-scalar or infinity shortcut. Its multipair branch detects raw 256-byte
infinity first, compacts those entries, and validates every remaining finite
point even if that point's scalar is zero. The candidate states precisely
those observable classifications. It deliberately does not claim the
parallel validation order used by native code.

Fp framing, Fp2 decoding, curve membership, subgroup membership, G2
multiplication, MSM, the standard native backend, and the zkEVM backend are
separate fields of MsmOracle. No theorem equates an oracle to either backend,
or establishes a cryptographic law. In particular, the model does not claim
native/zkEVM equivalence, low-level scalar reduction behavior, allocation,
metrics, registration, generic precompile wrapper behavior, gas exhaustion
handling, transaction composition, or block composition.

## Fixture evidence

The focused test class registers exactly these four checked-in assets. Their
SHA-256 digests and entry counts were independently recomputed:

| Asset | Entries | SHA-256 |
| --- | ---: | --- |
| [mul_G2_bls.json](../../../../../src/Nethermind/Nethermind.Evm.Test/PrecompileVectors/Bls/mul_G2_bls.json) | 11 | 2af1e85ed661cf12f8b3b9516bb2cc59cddd2332c2f907ac428c7a8da7235582 |
| [multiexp_G2_bls.json](../../../../../src/Nethermind/Nethermind.Evm.Test/PrecompileVectors/Bls/multiexp_G2_bls.json) | 14 | 957a6da84b113c21adabdb34beed9b3ac9a277d2711618ad258ec8929dc839f5 |
| [fail-mul_G2_bls.json](../../../../../src/Nethermind/Nethermind.Evm.Test/PrecompileVectors/Bls/fail-mul_G2_bls.json) | 7 | 2cd87cbadd772686936b3ce68e2f1f9931b2d4e250e46ea697608197d8a71306 |
| [fail-multiexp_G2_bls.json](../../../../../src/Nethermind/Nethermind.Evm.Test/PrecompileVectors/Bls/fail-multiexp_G2_bls.json) | 7 | 43bd9b6631ddf4e4849069f98e78a6408d35ffdfc1fb484a33205cae84a6e2df |

The vector file keeps independent expected-output and oracle-output tables
for the double, random-multiply, and two-term MSM known answers. They are
lifted respectively from successful mul_G2 and multiexp_G2 entries. It also
uses exact top-byte, field, off-curve, and subgroup literals from the failure
assets, with multipair forms corresponding to the fail-multiexp fixtures.
Fixture parsing is length-indexed and fail-closed: malformed or odd hex
parses to no bytes before any length proof can construct an encoded point.

There are 28 executable behavior vectors. They cover empty and four
near-boundary frame sizes; one- and two-pair successes; unrestricted,
zero, random, and above-subgroup-order scalars; all four rejection classes;
zero scalar before malformed point or subgroup; raw infinity alone, repeated,
and interleaved with both a nonzero and zero scalar; and invalid first/later
multipair entries. There are 11 exact gas boundary vectors and 25 local
mutation sentinels for metadata, framing, schedule entries and cap,
validation gates, scalar treatment, raw-infinity compaction, and every
independent known-answer table/oracle output path.

The model contains 23 named theorems. The vector module contains 9 named
theorems and 29 examples, including the 25 mutation sentinels. Direct
warning-as-error Lean checks and the targeted eight-job Lake build are
recorded for this leaf. These checks establish the finite executable model and
fixtures within the independently reviewed handwritten boundary; they are not
a production refinement or cryptographic proof.

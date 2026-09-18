# BLS12-381 Fp-to-G1 precompile leaf reference

`Bls12381FpToG1.lean` and `Bls12381FpToG1Vectors.lean` are a standalone,
handwritten Lean reference candidate for the standard-mainnet EIP-2537
`BLS12_MAP_FP_TO_G1` precompile at address `0x10`. They are deliberately not
imported into `Eip803x.lean`, the Lake manifest, or any production build. This
candidate is not an acceptance decision.

The specification is pinned to
[`ethereum/EIPs@1dd2558f.../EIPS/eip-2537.md`](https://github.com/ethereum/EIPs/blob/1dd2558f9a68d9453aed71c803fdda09d83c6e37/EIPS/eip-2537.md).
The production sources compared while writing it are
[`Bls12381FpToG1Precompile.cs`](../../../../../src/Nethermind/Nethermind.Evm.Precompiles/Bls12381FpToG1Precompile.cs),
the standard partial
[`std/Bls12381FpToG1Precompile.cs`](../../../../../src/Nethermind/Nethermind.Evm.Precompiles/std/Bls12381FpToG1Precompile.cs),
the zkEVM partial
[`zkevm/Bls12381FpToG1Precompile.cs`](../../../../../src/Nethermind/Nethermind.Evm.Precompiles/zkevm/Bls12381FpToG1Precompile.cs),
and the shared [`Eip2537.cs`](../../../../../src/Nethermind/Nethermind.Evm.Precompiles/Eip2537.cs).

## Modeled contract

The leaf fixes the address at `0x10` (`16`) and the name at
`BLS12_MAP_FP_TO_G1`. `supportsCaching` is `true`; normalization is the
identity. Registration is modeled as the address/name pair behind an
`eip2537Gated` flag, matching the production provider's EIP-2537 registration
surface. The schedule is explicit: `baseGasCost` reads a configurable
`Schedule.fixedGas`, `Schedule.amsterdam.fixedGas` is `5500`, and
`dataGasCost` is zero for every input.

Admission is exact: only a 64-byte input is decoded. The first 16 bytes are
required to equal zero padding and the remaining canonical field value is
exactly 48 bytes. `decodeInput?` rejects every other length before any field or
map oracle is consulted. `runDecoded` then checks padding, asks the explicit
`MapOracle.validField` boundary about the Fp value, and finally calls the
explicit `MapOracle.mapToG1` boundary. The model uses
`invalidFieldElementTopBytes` for nonzero padding and `invalidFieldElement`
for the separate field-oracle rejection; these names intentionally abstract
the production error mapping (the standard partial reports the shared
`G1PointSubgroup` error for a noncanonical field value, while the zkEVM
partial maps `TryDecodeFp` failure to `InvalidFieldElementTopBytes`).

`G1Output` contains two length-indexed 48-byte Fp coordinates. `encodeOutput`
serializes them as 16 zero bytes plus `x`, followed by 16 zero bytes plus `y`,
for exactly 128 bytes. The model proves both padding regions and the total
length independently of the map oracle.

The source audit also checked the surrounding surfaces: the blockchain
[`EthereumPrecompileProvider.cs`](../../../../../src/Nethermind/Nethermind.Blockchain/EthereumPrecompileProvider.cs)
maps address `0x10` to `Bls12381FpToG1Precompile.Instance`; precompile
[`Extensions.cs`](../../../../../src/Nethermind/Nethermind.Evm.Precompiles/Extensions.cs)
adds it only when `Bls12381Enabled` is active; and
[`ReleaseSpec.cs`](../../../../../src/Nethermind/Nethermind.Specs/ReleaseSpec.cs)
puts the address in the precompile cache set under the same EIP-2537 gate.
The default identity normalization and cache support come from
[`IPrecompile.cs`](../../../../../src/Nethermind/Nethermind.Evm/Precompiles/IPrecompile.cs).

## Fixtures

The vector file ingests all ten applicable literals from the vendored assets:
five success cases and five failure cases. The source assets were independently
checked as follows:

| asset | entries | SHA-256 |
| --- | ---: | --- |
| [`map_fp_to_G1_bls.json`](../../../../../src/Nethermind/Nethermind.Evm.Test/PrecompileVectors/Bls/map_fp_to_G1_bls.json) | 5 | `5669bf7e0e2a22a884aa8c2e4436db956216709bbe5c7ff6e982e54b393c5fd5` |
| [`fail-map_fp_to_G1_bls.json`](../../../../../src/Nethermind/Nethermind.Evm.Test/PrecompileVectors/Bls/fail-map_fp_to_G1_bls.json) | 5 | `2dd3304500423bdf5aa1fa1d3886307f32b88b3cc913727d7ebc8adf0c0e8323` |

The five success names are `bls_g1map_`, `bls_g1map_616263`,
`bls_g1map_6162636465663031`, `bls_g1map_713132385f717171`, and
`bls_g1map_613531325f616161`; each input is 64 bytes, each expected output is
128 bytes, and each asset gas field is 5500. The five failure names are
`bls_mapg1_empty_input`, `bls_mapg1_short_input`, `bls_mapg1_large_input`,
`bls_mapg1_top_bytes`, and `bls_invalid_fq_element`; their input widths are
0, 63, 65, 64, and 64 bytes respectively. The last two exercise nonzero top
padding and a noncanonical Fp value after zero padding. The failure asset has
empty expected-output strings, so the candidate records the corresponding
abstract error class rather than inventing an output literal.

Expected output literals and oracle output literals are separate declarations,
even though the five pairs are byte-for-byte equal. All ten vectors charge the
modeled 5500 base gas and zero data gas. The parser also has malformed/odd
hexadecimal sentinels (`"0"` and `"zz"`) that fail closed.

The exact declaration counts are:

- model: 17 theorems and 0 examples;
- vector file: 8 theorems, 27 examples, 10 vectors, and 15 mutation sentinels;
- combined: 25 theorems and 27 examples.

The 15 mutation sentinels cover address, name, caching, registration address,
fork gating, gas, input length, value offset, omitted padding validation,
validation-order reversal, omitted field validation, omitted output padding,
coordinate swapping, expected-output corruption, and oracle-output corruption.

## Checks run

Using the pinned Lean toolchain's `lake.exe` (it was not on `PATH`), the direct
warning-as-error checks passed:

```text
lake env lean -DwarningAsError=true Eip803x/Precompiles/Bls12381FpToG1.lean
lake env lean -DwarningAsError=true Eip803x/Precompiles/Bls12381FpToG1Vectors.lean
```

The targeted Lake checks also passed:

```text
lake build Eip803x.Precompiles.Bls12381FpToG1
lake build Eip803x.Precompiles.Bls12381FpToG1Vectors
```

No `dotnet` command was run. No source, asset, or production test file was
modified.

## Open obligations

This is a structural and fixture reference, not a cryptographic proof. Fp
modulus comparison beyond the finite fixture oracle, BLS12-381 field/curve
correctness, hash-to-curve/map-to-G1 correctness, subgroup guarantees, native
and zkEVM equivalence, and `EncodeRaw`/accelerator correctness remain open.
There is no refinement theorem connecting the model to either production
partial, and no proof of metrics, allocation, concurrency, exception behavior,
registration wiring, cache implementation, generic wrapper/frame effects,
account touch/rollback, or transaction/block composition. The source/assets
comparison found no confirmed production defect; the candidate remains
unintegrated and unaccepted pending those obligations.

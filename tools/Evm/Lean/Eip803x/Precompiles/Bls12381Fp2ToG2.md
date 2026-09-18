# BLS12_MAP_FP2_TO_G2 execution reference

## Status

This is an independently reviewed and accepted handwritten Lean reference,
imported by `Eip803x.lean`. It is not a production extraction or refinement.

The candidate fixes EIP-2537 address `0x11` (`17`), name
`BLS12_MAP_FP2_TO_G2`, and Amsterdam pricing `{ fixedGas := 23800,
dataGas := 0 }`. Both gas fields remain explicit `Schedule` inputs.

## Modeled contract and deliberate boundary

- The leaf accepts exactly 128 bytes: two ordered 64-byte Fp elements, each
  with 16 required-zero top bytes and one 48-byte value. It emits four padded
  64-byte G2 wire elements, exactly 256 bytes.
- The modeled order is length, first padding, first field predicate, second
  padding, second field predicate, then `mapFp2ToG2 first second`. Errors are
  explicit: invalid length, invalid top bytes, and invalid field element.
- `MapOracle.validField` is deliberately an arbitrary Boolean predicate. A
  refinement must prove it means exactly the big-endian BLS12-381 condition
  `value < p`; the known-answer oracle only recognizes the fixture values and
  is not a proof of that predicate for all values.
- `MapOracle.mapFp2ToG2` is the field/map boundary. `G2Output` preserves four
  opaque output wire positions, but this candidate does not prove the standard
  `G2.MapTo`/`EncodeRaw` permutation equals the ZK accelerator
  `Bls12381MapFp2ToG2`/`EncodeG2` permutation. That relation is explicitly
  unproved.
- `Metadata` and `metadataMatchesRegistration` make address, name, cache
  support, registration address/name, cached address, and the EIP-2537 gate
  observable together. Mutation checks alter the projection, not isolated
  constants.

## Production evidence

- `src/Nethermind/Nethermind.Evm.Precompiles/Bls12381Fp2ToG2Precompile.cs:19-29`
  pins `0x11`, name, `23800`, `0`, and `2 * LenFp` input framing.
- `.../std/Bls12381Fp2ToG2Precompile.cs:19-37` checks length, then
  short-circuits `ValidRawFp` first and second, calls `MapTo` with `[16..64]`
  then `[80..128]`, and returns `EncodeRaw`.
- `Eip2537.cs:270-300` provides raw G2 encoding and the 64/16/48 Fp
  padding/canonicality path. `Eip2537.zkevm.cs:23-29,42-59` and
  `zkevm/Bls12381Fp2ToG2Precompile.cs:15-37` provide the separate accelerator
  encoding/decode path whose output-order equivalence remains open.
- `Nethermind.Evm.Precompiles/Extensions.cs:42-52`,
  `Nethermind.Specs/ReleaseSpec.cs:202-211`,
  `Nethermind.Blockchain/EthereumPrecompileProvider.cs:30-37`, and
  `Nethermind.Evm/Precompiles/IPrecompile.cs:11-31` establish the gate,
  cached precompile address, provider registration, default caching, and
  normalization evidence.

## Vendored transcription and independent output stages

The vector module transcribes all five success and five failure entries from
the two local JSON assets. Every one of the 15 vendored fixture literals has a
proof-carrying `ParsedFixture`; its bytes are available only alongside
`parseHex literal = some bytes`. The empty fixture additionally proves
`parseHex emptyInputHex = some []`. A malformed fixture literal therefore
breaks its parse proof at Lean compilation; malformed non-fixture parser cases
remain checked as `none`.

Five separately spelled full `oracle*Hex` output literals are also parsed with
proofs. They are intentionally not aliases of the five vendor
`expected*Hex` literals. Each vector has three distinct stages:

```text
run(input)  ->  oracle result  ->  vendored expected result
```

`runMatchesOracle` and `oracleMatchesExpected` are separately proved for all
ten vectors. Oracle-only and expected-only mutation vectors independently
break the appropriate stage.

`SuccessAssetBinding` stores each exact success name, input hex/bytes with its
parse proof, output hex/bytes with its parse proof, and gas. `FailureAssetBinding`
does the same for each exact failure name/input, vendor error text, and modeled
error. The named
`vendored_*_binding` theorems bind all five success names/inputs/outputs and
all five failure names/inputs/error texts/errors to that transcription.

| asset | entries | external SHA-256 audit |
| --- | ---: | --- |
| `map_fp2_to_G2_bls.json` | 5 success | `00f39be8b922fa22a0d0f20b23dd270b9fe0882c5066873228492eea8eb520c8` |
| `fail-map_fp2_to_G2_bls.json` | 5 failure | `101e4e994c1c265a8f49b1ea220f7ee9e9b208a63473fcd86a55a0d5e4a2bd37` |

The SHA-256 comparison is external verifier/tooling evidence only; Lean does
not implement or prove SHA-256 binding of the source JSON files.

The top-byte vendored fixture is retained exactly, but it is not claimed to
prove general validation precedence: its shifted bytes and fixture-specific
field oracle make it only an asset-result check. Dedicated decoded sentinels
prove the two relevant orderings with exact errors: first-field-invalid plus
second-padding-invalid returns `invalidFieldElement`, and second-padding-invalid
plus second-field-invalid returns `invalidFieldElementTopBytes`.

## Coverage and validation

`Bls12381Fp2ToG2.lean` has 22 named theorems. The vector module has 43 named
theorems and 43 checked examples, including 21 parse-success/empty-parse
theorems, exact asset bindings, all vector stages, and 28 mutation/precedence
checks. Mutations cover the observable metadata projection, both gas fields,
offset/framing, padding and field ordering, map argument order, output padding
and wire order, and independent oracle/expected output corruption.

From `tools/Evm/Lean`, these commands pass with warnings treated as errors:

```powershell
$lake = 'C:\Users\flcl\.elan\toolchains\leanprover--lean4---v4.33.1\bin\lake.exe'
& $lake env lean -DwarningAsError=true Eip803x/Precompiles/Bls12381Fp2ToG2.lean
& $lake build Eip803x.Precompiles.Bls12381Fp2ToG2
& $lake env lean -DwarningAsError=true Eip803x/Precompiles/Bls12381Fp2ToG2Vectors.lean
& $lake build Eip803x.Precompiles.Bls12381Fp2ToG2Vectors
```

The fail-closed verifier checks both JSON hashes and each complete named
success/failure record association, including input, output, gas, failure text,
and the text-to-model-error mapping. Lean itself does not prove SHA-256. The
focused production suite passes 110/110 cases; it is differential evidence
rather than extraction or refinement.

## Open obligations

The reference does not prove BLS12-381 field arithmetic or `< p`, map-to-G2,
standard/ZK output permutation equivalence, cache implementation, metrics,
allocation, `Span`/`ReadOnlyMemory` slicing, `stackalloc`, CLR/ref-struct
behavior, generic precompile-call gas debit, call-frame failure translation, or
accelerator exceptional status. No production bug is asserted by this
handwritten reference.

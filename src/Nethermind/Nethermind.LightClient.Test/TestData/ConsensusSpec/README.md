# Consensus-specs light-client SSZ fixtures

The twelve compressed fixtures are unchanged `serialized.ssz_snappy` files from
[consensus-specs v1.7.0-beta.4](https://github.com/ethereum/consensus-specs/releases/tag/v1.7.0-beta.4),
`mainnet.tar.gz`, published on 2026-10-08. Each is `case_0` from:

The release tag resolves to commit `aa16bb4c156184e9548a997d53efaf2a228a6304`.

`tests/mainnet/{electra,fulu,gloas}/ssz_static/LightClient{Bootstrap,Update,FinalityUpdate,OptimisticUpdate}/ssz_random/case_0/`

`expected.json` records the upstream `roots.yaml` root, selected named fields
from `value.yaml`, and SHA-256 of each original compressed file. The offline
test checks the file digest, independent upstream hash-tree root, named beacon
and execution fields, and exact round-trip through the production fork-aware
wire codec. The random slots require an explicitly selected synthetic fork
schedule; they are not valid consensus updates or live network checkpoints.

These fixtures prove serialization conformance. The separate captured mainnet
Fulu fixture exercises real committee proofs, BLS signatures, tampering rejection
and journal replay. Neither establishes adversarial-network availability or
live Gloas interoperability.

# Functional proof devnet capture

This is functional evidence from two independent execution processes, followed by a separate two-EL/two-Lighthouse Kurtosis network with 32 validators. It is not a throughput or slot-capacity benchmark. The reproduction setup is in [the devnet guide](../../README.md); the real-beacon setup and Engine proof-field relay are documented [separately](../../consensus/README.md).

## Standalone Engine driver

[standalone-evidence.json.gz](standalone-evidence.json.gz) preserves the eight-block report, both unsuccessful 64-signature reports, summarized node receipts, native mutation inspections, and SHA-256 hashes of manifests, wrappers, full payload requests and proof bytes. The deployed Runner source was `5fae5c6e8c4bb6df6b825e8cc970b8e306052784`. Binary hashes are included; full requests, fixtures and JWT secrets remain untracked on the Runner host.

The driver alternated the producer, submitted each valid wrapper only to the other node, and observed its arrival through `lean/1` before building. Both nodes imported each block, returned successful receipts and selected matching canonical heads. Native inspections authenticated each original proof. Rehashed blocks with a corrupt proof or dependency commitment returned `INVALID`; all 16 tampered-wrapper submissions were rejected. Stale payload attributes were rejected without changing the final head.

| Case | Wrapper | Verified dependencies | Block proof bytes |
| --- | --- | --- | ---: |
| SPHINCS 1 | Direct | 1 signature | 298,912 |
| SPHINCS 1 | Recursive | 1 signature | 299,264 |
| Generic STARK 1 | Direct | 1 generic | 290,268 |
| Generic STARK 1 | Recursive | 1 generic | 292,572 |
| Mixed 4 | Direct | 4 signatures + 1 generic | 572,188 |
| Mixed 4 | Recursive | 4 signatures + 1 generic | 573,148 |
| SPHINCS 16 | Direct | 16 signatures | 282,848 |
| SPHINCS 16 | Recursive | 16 signatures | 283,104 |

The recorded build times include the driver's deliberate 10-second wait. Generic proofs are genuinely verified and carried, not recursively compressed. Block proofs were imported through Engine API; this does not demonstrate networked block-proof sidecars.

### Preserved 64-signature failures

Four valid 16-signature transactions were admitted and gossiped for a target block containing 64 distinct signature dependencies. Its fixtures overlap 42 dependencies from the earlier smoke run, so cached recursive coverage may be reused. This is not a fresh-64 proving measurement.

| Attempt | Improvement budget | Driver wait | Result |
| --- | ---: | ---: | --- |
| Original | Normal 3 seconds | 20 seconds | Returned transaction set differed from the full gossiped batch; improvement cancellation was observed. |
| Slow-prover retry | Explicit 120 seconds | 90 seconds | `engine_getPayloadV6` returned `UnknownPayload` after payload-cache cleanup. |

Neither attempt produced a verified 64-signature block in this capture. Increasing `--build-wait` does not extend Merge's independent improvement deadline. Payload cleanup also remains active when the improvement budget is enlarged. The standalone processes were stopped after the bounded retry. Later runtime fixes do not retroactively change these results.

## Real beacon and validator network

[consensus-evidence.json](consensus-evidence.json) records two later successful cases with normal 12-second slots and a 0.25 improvement fraction (3 seconds). [consensus-provenance.json](consensus-provenance.json) preserves the initial direct-signature receipts, the earlier incomplete recursive attempt, and the deployed source/binary hashes.

| Case | EL block | Beacon slot | Node receipts | Proof bytes |
| --- | ---: | ---: | --- | ---: |
| Direct SPHINCS 1, nonce 0 | 31 | 32 | Both successful, same block hash | 265,528 |
| Recursive SPHINCS 1, nonce 1 | 153 | 160 | Both successful, same block hash | 264,624 |
| Direct generic STARK 1, nonce 2 | 154 | 161 | Both successful, same block hash | 290,268 |

The later cases used the original `5fae` deployment plus the exact verified-parent-reuse source delta recorded in the provenance file, with `Nethermind.Consensus.dll` SHA-256 `5bc3d3e0e63890c82ea255008539a33fd36c965049473116a051922566a7f117`. They were not captured with an executable rebuilt from the later final commit. The saved beacon execution bids commit to the exact receipt block hashes. Both beacon nodes had the same non-optimistic canonical head. The later finality snapshot records finalized epoch 8 with matching checkpoint roots on both nodes: the initial slot-32 block and the later slots 160/161 are finalized. The capture records the finality observation time separately from the original case timings.

The recorded 18.691-second and 12.131-second intervals end when both receipts are observed; they include slot scheduling and are not native proof-generation timings. The relay preserves proof fields across Lighthouse's payload encoding using a bounded block-hash-keyed cache. These results exercise real beacon/validator driving through that local bridge; they do not claim native CL serialization support for the additional proof fields or proof-bearing inclusion lists.

## Compact export

Run `capture.py --runtime-root=<runtime>` on the Runner host and retain its JSON output. It reads only fixed driver/manifest paths, summaries and public hashes; it never opens JWT or configuration files. Artifact SHA-256 values permit comparison with the retained untracked originals. Dependency-set SHA-256 values hash sorted manifest triples and are provenance checks, not replacements for EIP-8288's Keccak dependency commitment.

The standalone JSON is deterministically gzip-compressed; `gzip -dc standalone-evidence.json.gz` restores every captured value. [Checksums](standalone-evidence.sha256) cover both compressed and original bytes.

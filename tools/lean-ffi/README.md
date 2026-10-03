# EIP-8288 native Lean integration

This library pins official [leanVM](https://github.com/leanEthereum/leanVM/tree/f33f31bf7c1191667e29a68a3acae63b9164c1c6)
`block-deps-hash` revision `f33f31bf7c1191667e29a68a3acae63b9164c1c6`, atop the
NiceTry/Daisugi branch (distinct from upstream `main`). It verifies real Keccak SPHINCS
signatures and binary-field leanVM proofs. No hash-based proof substitute is accepted.

## Build and test

Rust 1.99+ is required for the pinned upstream APIs.

```sh
cargo test --release --locked --manifest-path tools/lean-ffi/Cargo.toml -- --test-threads=1
dotnet run --project src/Nethermind/Nethermind.Crypto.LeanFfi.Test/Nethermind.Crypto.LeanFfi.Test.csproj -c release -p:BuildLeanFfi=true
dotnet publish src/Nethermind/Nethermind.Runner/Nethermind.Runner.csproj -c release -p:BuildLeanFfi=true
```

`BuildLeanFfi=true` builds and copies the host native library into consumer build/publish
outputs and rejects a different target RID. Build on each target platform; cross compilation
and distribution remain release packaging work. Ordinary builds do not require Rust.
A chain with a scheduled EIP-8288 fork checks the native ABI and guest key at startup
and fails with installation instructions if the backend is unavailable. Other chains do
not load it. Verification also fails closed; proving requires the library.

`cargo run --release --locked --manifest-path tools/lean-ffi/Cargo.toml --example fixtures -- <directory>`
produces genuine test signatures/proofs and the pinned recursive verification key. The live
FFI tests build these fixtures with `BuildLeanFfi=true` and fail if the native library cannot load.
The main solution compiles the test project without requiring Rust. Without the opt-in property,
the native suite is explicitly skipped. With it, the backend and fixtures are required and
missing libraries fail the run.

The C ABI is version 4, with Cdecl calls, 32-byte hashes/keys and `size_t` buffer lengths.
`nlean_limits` writes nine u32 values (proof bytes, dependencies, recursive children,
SPHINCS witness bytes, generic STARK count, instructions, operand offset, aggregation input bytes,
SPHINCS guest proof bytes); startup
checks these against the managed protocol bounds before accepting work.
Verification/proving return 1 on success and 0 on failure. Callers initialize proof output
pointers to null; successful proof allocations must be released once with `nlean_free`
using the returned pointer and length. Verification contains upstream panics and never
accepts malformed inputs. Panic diagnostics on ABI callers and the pinned parallel worker
pool are suppressed while native calls are active; the previous hook remains active for
unrelated threads and outside native calls. The upstream pool resumes worker panics on the
dispatcher, where the ABI catches them.

## Prototype wire format

All framing integers are unsigned 32-bit little-endian. Proofs and individual witnesses
are bounded to 8 MiB; aggregation input to 18 MiB so two maximum-sized child proofs and
their dependency/discard metadata fit a recursive step. This is an input-buffer limit,
not a process memory bound. Dependency lists are bounded to 4096; recursive children and carried generic
STARKs to 16 each. This format and the
pinned key are prototype protocol choices, pending finalized EIP-8288 encodings.

The serialized SPHINCS guest proof has an explicit 2 MiB acceptance bound. Every accepted
aggregate reserves `16 + 96*dependencies + 100*generic_claims + generic_witness_bytes`
plus 2 MiB when SPHINCS claims are present, and this total must fit 8 MiB. Verification
and proving enforce the same rule; a standalone generic witness must fit its 212-byte
single-claim envelope overhead. The 2 MiB cap is a prototype consensus choice, not a
proven maximum of upstream proof sizes or a memory guarantee. Larger upstream proofs
fall outside this profile and fail closed.

The [EIP](https://eips.ethereum.org/EIPS/eip-8288#recursive-stark-header-entry) requires the proof in the block header, so the header database and caches retain it.
Generic STARK witnesses can make each header approach 8 MiB. On proof-bearing chains,
header serving loads full headers incrementally and stops at a 9 MiB response budget
before loading the next header. Headers with proofs above 64 KiB use a separate
32 MiB / 128-header LRU with owned proof snapshots; repeated reads avoid DB decoding
without multiplying the ordinary cache's capacity by large proof size. Large proofs reduce batch size. This trades header-sync throughput and storage for the prototype's
current header format; a finalized sidecar format would require a protocol change.
ETH header responses retain their standard whole-message encoding, so a large
proof-bearing header can still delay other traffic on that connection.
The decoded block cache skips proofs above 64 KiB. Invalid-block diagnostics retain
large proof-bearing records in a separate eight-entry / 64 MiB tier, counting header
proofs and inclusion-list proof/dependency buffers and returning owned copies.

* **SPHINCS witness:** public key (32 bytes), signature (6176 bytes). The dependency key
  is Keccak-256 of the public key. Both this hash and the signature over `data_hash` are checked.
* **leanSTARK witness:** bytecode blob length, canonical bytecode, then fixed-integer bincode
  serialization of the upstream CPU proof (no trailing bytes). The dependency key is
  Keccak-256 of that canonical bytecode. `data_hash` is the VM's 32-byte public commitment,
  packed into two 128-bit field cells; the guest must constrain the relation it represents.
  At most 16384 instructions and operand offsets through 65535 are accepted; the offset
  limit bounds upstream assembly's g-power table. Each instruction is a 45-byte record: opcode
  byte, four u32 operands, three u64 immediate limbs, then u32 BLAKE2s metadata operand.
  Opcodes 0–6 are XOR, MUL, SET, DEREF, JUMP, BLAKE2s, SHA3. Unused fields are zero. SHA3 packs its four extra message offsets
  in the first two immediate limbs, capacity/output offsets in the third, and its digest flag
  in the metadata field.
* **aggregation input:** direct count then `(96-byte dependency, witness blob)` pairs;
  child count then `(dependency count/triples, proof blob)` pairs; discard count/triples.
* **aggregate:** `NLR2`, canonical dependency count/triples, upstream EthereumProof blob
  (empty when there are no SPHINCS claims), generic-STARK count then `(dependency, witness blob)`
  pairs in canonical dependency order. A blob is its length followed by bytes.

The recursive key is the actual upstream guest's Fiat-Shamir seed, pinned in
`Eip8288Constants.AggregatedVk`. Root verification compares full dependency triples against
cryptographically verified SPHINCS claims, verifies every generic STARK, and recomputes the
canonical Keccak dependency commitment. Proving verifies every direct witness and child,
then applies union, deduplication and discard selection. Caller-supplied dependency metadata
cannot create a claim absent from the verified witnesses.

Upstream `sphincs_deps_hash` is BLAKE2s over sorted distinct `message || raw_public_key`
(64-byte claims); it is not the EIP adapter's Keccak commitment over 96-byte triples.
The adapter verifies that native statement with `verify_sphincs_deps`, reconstructs
`Keccak(public_key)` from the verified claim preimages, and requires exact equality
with the EIP dependency set. Neither digest is substituted for the other. With no
SPHINCS claims the guest blob must be empty; an empty dependency set has exactly the
16-byte canonical `NLR2` envelope and no guest proof. Generic-only envelopes still
verify every generic witness. The guest key is pinned, not trusted dynamically at startup.

## Proving resources

This Keccak profile has a much larger raw proving witness than the former BLAKE2s
profile. The adapter verifies every input, proves at most four unique selected signatures
per leaf, and combines at most two children at each recursive node. Discards are applied
to declared claims at every level. Larger signature sets use genuine recursive proofs
rather than one large raw witness. Upstream's direct 100-signature memory estimate is
about 57 GiB; historical BLAKE2s benchmark results do not describe this profile.

On the 32 GiB ARM development host, fresh-process direct batches of 2/4/8 signatures
used 1.28/2.07/3.92 GiB peak RSS. Four-signature leaves produced a verified 16-signature
aggregate in 4.11 s with 4.52 GiB process peak, versus 15.30 s and 4.95 GiB with singleton
leaves. Four is the conservative default for smaller CI hosts; these observations are
not a hard memory bound. Proving is serialized within each aggregation request.
Reproduce sizing with `cargo run --release --example resource_probe -- 4 direct` or
`cargo run --release --example resource_probe -- 16` from this directory; signature
generation is outside the reported proving time but included in process peak RSS.

## Recursive compression limit

The upstream recursive guest compresses SPHINCS claims recursively. Its bytecode is fixed;
it cannot recursively verify arbitrary leanSTARK programs. Generic STARK witnesses are
therefore carried in the aggregate envelope and cryptographically reverified at the root.
Their size is not compressed. Extending the guest to recursively verify arbitrary bytecode
is separate work; this implementation does not claim that functionality.

Before serde decoding, a zero-allocation pass checks every pinned wire vector length,
claims count and nested Merkle row against available bytes. It caps opening phases at
128, rows per phase at 4096, and estimated decoded allocations at 32 MiB. The upstream
CPU verifier bounds transcript instance dimensions before reductions; it does not
allocate the claimed execution trace. Each generic proof is still verified separately,
so the 16-proof aggregate bound limits their accumulated validation cost.

The dependency, generic-proof, program and decoding limits are prototype
backend acceptance bounds beyond the unrestricted EIP. Block/proof interoperability
requires peers to share these bounds.

The upstream project remains experimental and unaudited. This integration is a prototype,
with a pinned backend and explicit wire choices, rather than a finalized network protocol.

## Running an EIP-8288 node

Build the native library and copy it beside the Nethermind executable (or put its directory
on the host's native library search path). The native backend is always selected for
EIP-8288; missing libraries fail startup on configured proof-bearing chains, and invalid
proofs fail validation. There is no placeholder switch.

Prototype proof payloads use the JSON Engine API and RLP transport. Standard Engine SSZ
schemas cannot carry block or inclusion-list proofs; proof-bearing SSZ payloads are rejected
with `UnsupportedFork` rather than losing their proofs.

Enable EIP-8141 and EIP-8288 in a development chainspec with `eip8288TransitionTimestamp`,
or the geth genesis key `eip8288PrototypeTime`. The named `Eip8288Prototype` / `eip8288PrototypeTime` also enables master's EIP-8250 keyed
nonces, EIP-8272 recent roots and EIP-7906 POST_TX frames. Frame transactions use explicit
`nonce_keys`; `[0]` retains account-nonce behavior. Individual chainspec transition fields
remain independently configurable. Recent roots follow master's older envelope-reference /
`RECENTROOTREFLOAD` draft, with existing commitment and slot-age checks; the newer EIP-8272
canonical VERIFY contract remains TBD and is not implemented here.

This prototype uses dependency frame mode
**4**, because current EIP-7906 uses mode 3 for `POST_TX`. Dependencies remain 96-byte triples;
their commitment is Keccak-256 over the lexicographically sorted, deduplicated set. Gas is
charged for every declaration, including duplicates.

The `eth` JSON-RPC module provides:

* `eth_sendProofWrapper("0x<RLP>")`: verify a mode 0 or mode 1 wrapper, retain its witnesses,
  and submit its transactions. It returns transaction hashes. Hash-only entries must already
  resolve in the local pool.
* `eth_getProofWrapper()`: return a hexadecimal RLP mode 1 wrapper for proof-backed pending
  transactions. Selection rotates across the pool when wrapper limits prevent including all
  candidates; unchanged selections reuse an already verified aggregate.
* `eth_sendProofInclusionList("0x<RLP>")`: verify a self-contained proof-bearing inclusion
  list and submit its transactions. Consensus-layer inclusion-list obligations are transported
  through the Engine payload attributes, rather than being created by this RPC call.

A raw dependency transaction without retained valid witnesses is rejected. Builders snapshot
selected witnesses, discard dependencies outside the selected transaction set, and verify the
produced header proof. Selection reserves proof gas from both execution and state gas budgets.
Managed proving folds at most four direct witnesses or two recursive children per native call;
4 MiB is the direct-leaf batching target, not a block-wide witness limit. Native inputs are bounded
to 18 MiB, output proofs to 8 MiB, and selected plus discarded coverage to 4096 dependencies.

Required witness storage is bounded to 64 MiB / 1024 records. Each direct dependency has its own
record, so rejected entries release their reserved witnesses. Recursive proofs are indivisible:
the complete proof and declared dependency metadata count toward both the global bound and a
12 MiB pinned-byte quota per sender. Shared records count once per sender. Admission reserves
capacity and sender quota before insertion; pending transactions pin witnesses until removal,
replacement or shutdown. Full protected capacity defers new admission. Multiple funded senders
can still fill this finite pending-witness budget. Dependency-bearing blob transactions are
removed on restart unless their witnesses are independently available. Generated aggregates use
a separate 64 MiB / 1024-record LRU that cannot replace required coverage.

RPC and peer ingress share one bounded native-verification gate to bound verification CPU and decoding work;
peer ingress retries temporary contention for a bounded interval. Inclusion-list package
submission through RPC validates a complete pool-admission package. Consensus eligibility instead filters
entries individually, preserving ordinary and zero-dependency entries when proof coverage fails.
The Network project references the shared Consensus admission service so RPC and negotiated
proof gossip use the same verification, pool insertion and witness-retention rules.

## Negotiated proof gossip

When the prototype fork is active at the node's head, it advertises only `lean/2`
alongside normal Ethereum capabilities. The obsolete whole-object `lean/1` fallback
is disabled so proof gossip cannot stall ETH traffic behind a large single write. The existing protocol registry
shares negotiation and shutdown with Ethereum handlers, using the Consensus proof
admission service and background scheduler.

Message 0 is a 72-byte status: chain ID (u64 big-endian), genesis hash (32 bytes), pinned
recursive guest key (32 bytes). All three must match before message 1 is accepted;
a mismatch disables only lean, preserving the session's other protocols.
The RLP mempool wrapper includes full transactions and is bounded by 10 MiB and
4096 transactions. Outgoing selection reserves 8 MiB for the proof before encoding
transactions.

`lean/2` streams independent chunks: a 48-byte header holds the whole-wrapper
Keccak commitment and big-endian total length, index, count and chunk size, followed
by chunk bytes. The default chunk is 64 KiB, with 16/32/64/128 KiB supported and
128 KiB as the maximum. Geometry is checked before allocation. The peer supplies an
unauthenticated commitment: matching it checks received bytes, not proof validity. The complete wrapper's proof and transactions
still pass shared admission before gaining proof-backed pool coverage. This is an
[EIP-8411](https://eips.ethereum.org/EIPS/eip-8411)-inspired bounded transfer, without
signed bids or Merkle authentication of individual chunks.

A shared 64 MiB / 1024-object budget covers received fragment memory, assembly metadata,
completion copies and completed wrappers queued or undergoing verification. Incomplete
fragment memory is capped at 48 MiB, preserving completion headroom within the total budget;
single-chunk complete objects can use that headroom without an incomplete reservation. Advertised
length never reserves or allocates a full wrapper: a 16 KiB start retains that fragment
and bounded metadata. The final contiguous buffer is reserved before allocation; fragment
memory is released after copying. Each peer retains at most two objects and approximately
20 MiB persistently; one temporary completion copy can raise its charge to approximately
30 MiB under the global cap, so two maximum wrappers can finish while admission is pending. Local quota or rate pressure drops incomplete
streams without penalizing the peer. TCP streams must start at index zero and advance in
order; unknown continuation chunks are ignored, and gaps within an accepted stream are
malformed. This prevents dropped starts from creating assemblies that can never complete.
Incomplete objects expire after 30 seconds without progress or five minutes absolutely.
Duplicates do not extend their lifetime, timer expiry needs no inbound traffic, and repeated
abandonment disconnects the peer, including streams abandoned more than five minutes apart. Shutdown and cancelled admission release leases; a completed
commitment is suppressed only while its admission buffer remains retained, so cancellation
can retry. Per-peer wire bytes and message counts are bounded before copying, allowing two
maximum wrappers per second including every chunk header. Each verification returns pending
work to the shared scheduler. Invalid proofs or encodings disconnect the peer; ordinary pool
rejection does not.
Busy verification retains the charged wrapper for cancellable retries, up to five seconds;
RPC native validation runs asynchronously under the same bounded admission gate.

One node-wide worker aggregates eligible pending transactions every second. Bounded
selection rotates across disjoint bounded groups; unchanged dependency sets reuse
verified proofs. Each peer keeps one active transfer and the latest pending selection;
a cadence never cancels an active object. Chunk sends await actual channel writes and
yield between chunks so control and ETH traffic can interleave. A whole transfer is
memoized only after every write succeeds. Unchanged wrappers refresh every 30–35 seconds
with per-peer jitter, recovering dropped queued work without admission acknowledgements.
Generic STARK witnesses remain carried and verified. Each peer remembers at most 64
recent delivered hashes. Only admitted transactions retain witness coverage.
`eth_getProofWrapper` returns a copy of the background-produced wrapper without invoking
the prover. Managed aggregation folds at most four direct witnesses or two children
per call and checks cancellation before and after each call. An individual native call
remains uninterruptible; shutdown cancels peer work and joins the worker.

During channel backpressure, the shared sender retains up to 64 non-bulk messages
within 12 MiB and drains them before bulk writes. Control responses wait for the current
chunk's compressed frame to drain. The RLPx merger still accepts one fragmented context
at a time; application chunks are complete independent messages rather than interleaved
fragments of one RLPx object.

## Proof-bearing inclusion lists

With both EIP-8288 and EIP-7805 active, FCUv5 payload attributes and newPayloadV6's
execution payload accept `inclusionListRecursiveStark: {starkProof, blockDepsHash}` and
`inclusionListProvenDependencies`, a hex string concatenating sorted, deduplicated 96-byte
triples (at most 4096 / 384 KiB). The explicit metadata hashes exactly to the proof's public
commitment and is required for dependency-bearing entries. Proof-bearing FOCIL RLP is
`[transactions, [stark_proof, deps_hash, proven_dependencies]]`; this prototype extension
makes membership independently checkable when a committee contributes a bad entry.
Verification precedes mandatory prefix checks. Malformed frames and frames declaring any
uncovered dependency are ineligible; ordinary and zero-dependency frame obligations remain
when the package proof is invalid or missing. Valid covered frames retain their obligations
in a mixed list. Builders and validators use the same selection. Each build owns one decoded
snapshot and its verified parent proof even when shared witness storage is full. Bounded input
snapshots are taken once to prevent caller mutation; later improvements reuse decoded entries
and production order. `getPayload` echoes neither proof nor metadata; the consensus client
supplies its own sidecar to `newPayload`. The CL can construct it through native aggregation
or the proof-wrapper path.
Legacy inclusion sources and producers without an inclusion source use mutually exclusive
decoding fallbacks; the proof-aware source overrides that path with its prepared snapshot.
Repeated package checks reuse a 64-entry positive-verdict cache per backend, keyed by the
dependency commitment and proof hash, without retaining proof buffers.
`engine_getInclusionListV1` retains its existing non-frame candidate sampling; externally
formed proof-bearing frame inclusion lists are validated and enforced.

The separate [frame/FOCIL PR #13590](https://github.com/NethermindEth/nethermind/pull/13590)
adds per-includer VERIFY budgets, claimed evaluation positions and BAL replay. It overlaps
this branch's frame eligibility checks and Engine plumbing. Integration should reuse its
eligibility engine, replacing this branch's end-state prefix simulation while retaining
Lean proof admission and dependency gas accounting. This branch remains based on master;
it does not yet include that open draft.

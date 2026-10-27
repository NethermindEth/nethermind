# EIP-8288 native Lean integration

This adapter uses a temporary [leanVM mixed-recursion fork](https://github.com/Marchhill/leanVM/tree/08031fc10f407f606120358e48437a783dba5bc9),
based on the Daisugi-compatible `f33f31bf7c1191667e29a68a3acae63b9164c1c6` revision.
The fork adds one recursive guest for Keccak SPHINCS claims and its own child proofs;
`Cargo.lock` pins the exact dependency revision. Upstream replacement belongs behind the
existing `ILeanProofVerifier` interface and thin C ABI. Proofs, guest key and native bounds
must change together when the guest changes. No hash-based proof substitute is accepted.

## Build and test

Rust 1.99+ is required for the pinned upstream APIs.

```sh
export LEANVM_NUM_THREADS=1
(cd tools/lean-ffi && cargo test --release --locked -- --test-threads=1)
dotnet run --project src/Nethermind/Nethermind.Crypto.LeanFfi.Test/Nethermind.Crypto.LeanFfi.Test.csproj -c release -p:BuildLeanFfi=true
dotnet publish src/Nethermind/Nethermind.Runner/Nethermind.Runner.csproj -c release -p:BuildLeanFfi=true
```

Cargo runs from `tools/lean-ffi` so `.cargo/config.toml` applies `-C target-cpu=native`: the
field arithmetic needs carry-less multiply (`pclmulqdq` on x86_64, `aes` on aarch64) and is
about 68× slower without it. An environment `RUSTFLAGS` replaces those flags instead of adding
to them, so it must keep `target-cpu=native` (or the features); the crate refuses to build
otherwise. Because the library targets the build host's CPU, a `BuildLeanFfi` publish can crash
with an illegal instruction (SIGILL) on an older CPU: build on the oldest CPU that will run it, or
set `RUSTFLAGS` to that CPU plus the carry-less multiply feature (for example
`-C target-cpu=x86-64-v3 -C target-feature=+pclmulqdq`). Proving uses leanVM's arena allocator, which recycles the prover's transient buffers
across proofs; it allows one proof per process at a time, which the backend's prover lock ensures.

`BuildLeanFfi=true` builds and copies the host native library into consumer build/publish
outputs and rejects a different target RID. Build on each target platform; cross compilation
and distribution remain release packaging work. Ordinary builds do not require Rust.
A chain with a scheduled EIP-8288 fork checks the native ABI and guest key at startup
and fails with installation instructions if the backend is unavailable. Other chains do
not load it. Verification also fails closed; proving requires the library.

`cargo run --release --locked --example fixtures -- <directory>`, run from `tools/lean-ffi`,
produces genuine test signatures/proofs and the pinned recursive verification key. The live
FFI tests build these fixtures with `BuildLeanFfi=true` and fail if the native library cannot load.
The main solution compiles the test project without requiring Rust. Without the opt-in property,
the native suite is explicitly skipped. With it, the backend and fixtures are required and
missing libraries fail the run.

The C ABI is version 8, with Cdecl calls, 32-byte hashes/keys and `size_t` buffer lengths.
`nlean_limits` writes six u32 values (proof bytes, dependencies, recursive children,
SPHINCS witness bytes, aggregation input bytes, mixed guest proof bytes); startup
checks these against the managed protocol bounds before accepting work.
Verification/proving return 1 on success, 0 on failure and -1 for a contained panic. Callers initialize proof output
pointers to null; successful proof allocations must be released once with `nlean_free`
using the returned pointer and length. Verification contains upstream panics and never
accepts malformed inputs. Panic diagnostics on ABI callers and the pinned parallel worker
pool are suppressed while native calls are active; the previous hook remains active for
unrelated threads and outside native calls. The upstream pool resumes worker panics on the
dispatcher, where the ABI catches them.

## Prototype wire format

All framing integers are unsigned 32-bit little-endian. The native backend bounds proofs and
individual witnesses to 8 MiB and aggregation input to 18 MiB; managed callers first apply
the local 1.25 MiB pinned-prover bound (`Eip8288Constants.MaxAggregateProofBytes`, not an EIP-8288
constant) to every aggregate proof. This is an input-buffer limit, not a process memory bound.
Dependency lists are bounded to 256 and recursive children to 16. This format and the
pinned key are prototype protocol choices, pending finalized EIP-8288 encodings.

The serialized mixed guest proof is the whole `stark_proof`; an empty dependency list has an
empty `stark_proof`. Blocks, wrappers and inclusion-list proofs reject a `stark_proof` above
that local bound before decoding or verification; measured proofs of this guest are
265–300 KB. These bounds are not process memory guarantees.

The current EIP-8288 companion places `stark_proof` in the block body and commits to
`keccak256(stark_proof)` and `block_deps_hash` as two flat 32-byte header fields. Header synchronization therefore
uses compact commitments. `eth` `BlockBodies` carry `stark_proof` after the existing fields from
the activating fork on every `eth` version, as for withdrawals. Block decoding and validation
enforce the local proof size bound, the proof hash, and the exact dependency union, verifying
under the block fork's `AGGREGATED_VK`. Engine payloads and payload-body responses
carry a single `starkProof` field; the execution layer derives the dependency commitment from
the payload transactions. An empty dependency union requires an empty proof.
The decoded block cache skips proofs above 64 KiB. Invalid-block diagnostics retain large
proof-bearing records in a separate eight-entry / 64 MiB tier, counting body proofs and
returning owned copies.

A zero dependency `data_hash` resolves to the containing frame transaction's canonical
signature hash before sorting and deduplication, so a zero leanSPHINCS message cannot be
declared. Raw frame bytes remain zero for
signing and `FRAMEDATACOPY`; wrapper, inclusion-list and block proof statements carry the
resolved dependency.

* **SPHINCS witness:** public key (32 bytes), signature (6176 bytes). As in EIP-8288, the
  dependency key is the public key itself, root then public parameter. Both the key and the
  signature over `data_hash` are checked.
* **leanSTARK (`0x11`):** reserved by EIP-8288 and rejected wherever a dependency is decoded.
* **aggregation input:** direct count then `(96-byte dependency, witness blob)` pairs;
  child count then `(dependency count/triples, proof blob)` pairs; discard count/triples.
* **aggregate:** the EIP-8288 `stark_proof`, one mixed guest proof without dependencies
  or raw witnesses. It verifies against `deps_hash` and `AGGREGATED_VK`
  alone; a child's dependencies come from the aggregation input. Proving an empty list
  returns an empty `stark_proof`, which callers check without the backend.

The recursive key is the actual mixed guest's Fiat-Shamir seed, pinned in
`Eip8288Constants.AggregatedVk`. The guest authenticates signature claims, verifies child mixed proofs, and applies union, deduplication
and discard selection. The adapter requires exact canonical EIP dependency triples and
recomputes their `get_deps_hash`; caller metadata cannot create an unproved claim.
Every supplied raw witness and child proof is authenticated even when another input already
covers the same claim. An unchanged verified statement can reuse its parent proof without
executing another recursive proving step.

`get_deps_hash` is BLAKE2s-256 over the canonical dependencies' 64-byte
`data_hash || verification_key` entries. The entries omit the scheme, which is sound only
because leanSPHINCS (`0x10`) is the one accepted scheme; the guest checks every entry of the
selected list and of each child list, discarded entries included. Startup checks ABI 8, the
pinned key and the six bounds together. New devnets require fresh chain and database namespaces.

The latest companion specification has no block dependency quota: blocks are bounded by gas, and wrappers in both modes by `MAX_LEANSIG_DEPS_PER_WRAPPER` and `MAX_WRAPPER_BYTES`. This prototype guest's 256-dependency limit is local; this key cannot prove every set the draft permits, so this prototype is not full-capacity conformance. Local inability to prove or retain a set is not an inclusion-list omission excuse. Full activation requires a verifier and proof format supporting every set the fork's gas rules permit, with the complete construction, including recursive merges and discards, benchmarked against the slot deadline.

## Proving resources

This Keccak profile has a much larger raw proving witness than the former BLAKE2s
profile. The adapter verifies every input, proves at most four unique selected signatures
per leaf, and combines at most two children at each recursive node. Discards are applied
to declared claims at every level. Larger signature sets use genuine recursive proofs
rather than one large raw witness. Small steps bound witness growth without guaranteeing
process memory usage; proving calls are serialized within the native backend. The pinned
Rust toolchain is selected from this directory, including MSBuild's Cargo invocations.
CI compiles for its host CPU and restores compiled artifacts only when CPU features,
architecture, toolchain and locked dependencies match.

## Proof verification limits

Before decoding, a zero-allocation pass checks proof vector lengths and transcript geometry
against available bytes and the selected guest profile. Decoding bounds
are prototype acceptance choices beyond the unrestricted EIP; interoperating nodes must
share the pinned guest, format and bounds.

The mixed profile uses PCS inverse-rate log 1. Mixed child proofs permit committed-polynomial
log size at most 27 and bus log size at most 26. The host and guest enforce these geometry
bounds, in addition to dependency and serialized-byte limits. A valid unrestricted upstream proof outside this profile is not admitted.

The upstream project and temporary guest remain experimental and unaudited. The output
cap and bounded native inputs do not bound native workspace or RSS. The adapter's
four-direct/two-child fold policy reduces individual work without guaranteeing that every
256-claim workload fits available RAM or a slot deadline.

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
their commitment is `get_deps_hash` over the lexicographically sorted, deduplicated set. Gas is
charged for every declaration, including duplicates.

The `eth` JSON-RPC module provides:

* `eth_sendProofWrapper("0x<RLP>")`: verify a mode 0 or mode 1 wrapper, which carries its full
  transactions, retain its witnesses, and submit its transactions. It returns transaction hashes.
* `eth_getProofWrapper()`: return a hexadecimal RLP mode 1 wrapper for proof-backed pending
  transactions. Selection rotates across the pool when wrapper limits prevent including all
  candidates; unchanged selections reuse an already verified aggregate.

A raw dependency transaction without retained valid witnesses is rejected. Builders snapshot
selected witnesses, discard dependencies outside the selected transaction set, and verify the
produced block proof. Selection reserves proof gas from both execution and state gas budgets.
Managed proving folds at most four direct witnesses or two recursive children per native call;
4 MiB is the direct-leaf batching target, not a block-wide witness limit. Native inputs are bounded
to 18 MiB and output proofs to the local aggregate proof bound. Each encoded dependency list and the selected
output are bounded to 256 dependencies; inputs may jointly cover more claims when
the discarded subset brings the output within that bound.
A verified recursive source covers duplicate claims without retaining or selecting
raw witness lengths for the output. Supplied raw duplicates still require authentication;
coverage is not a shortcut around witness validation. Fresh-claim throughput and process
memory measurements belong in the stacked
[benchmark PR](https://github.com/NethermindEth/nethermind/pull/14220), with the source guest
profile recorded for each capture.

Required witness storage is bounded to 64 MiB / 32768 records. The record ceiling does not
guarantee coverage for a full transaction pool: the byte bound fits about 10.6k distinct
6208-byte SPHINCS witnesses plus their 96-byte dependency records, about 665 transactions
with 16 distinct witnesses each. Shared dependencies and larger proofs change that capacity.
Each direct dependency has its own
record, so rejected entries release their reserved witnesses. Recursive proofs are indivisible:
the complete proof and declared dependency metadata count toward both the global bound and a
12 MiB / 256-record pinned quota per sender. Shared records count once per sender. Admission reserves
capacity and sender quota before insertion; pending transactions pin witnesses until removal,
replacement or shutdown. Full protected capacity defers new admission. Multiple funded senders
can still fill this finite pending-witness budget. Dependency-bearing blob transactions are
removed on restart unless their witnesses are independently available. Generated aggregates use
a separate 64 MiB / 1024-record LRU that cannot replace required coverage.

RPC and peer ingress share one bounded native-verification gate to bound verification CPU and decoding work;
peer ingress retries temporary contention for a bounded interval. The Network project references
the shared Consensus admission service so RPC and `eth/73` peers use the same verification, pool
insertion and witness-retention rules.

Wrappers are validated under a locally predicted target fork, the fork active at `head.timestamp + L`, where
`L = (head.timestamp - parent.timestamp) / (head.slot_number - parent.slot_number)` is the local
EIP-7843 slot-length estimate in integer seconds (the head's fork without a parent or when slot
numbers do not increase). The target fork
fixes the verifier: `AGGREGATED_VK` for mode-1 proofs and each scheme's parameters for
mode-0 witnesses. Two bounded local verdict caches hold direct-witness verdicts keyed by
`(dep, keccak256(witness), scheme_params)` and aggregate verdicts keyed by
`(keccak256(stark_proof), deps_hash, AGGREGATED_VK)`; a lookup consults only the cache of the
operation it would run. When the head reaches a target fork whose verifier differs, every held
aggregate proof and mode-1 wrapper is dropped, mode-0 witnesses are kept only if they re-validate,
pending transactions left without coverage are evicted, and nothing validated under the previous
verifier is announced or served. Development chains schedule such a fork with the chainspec
parameters `eip8288AggregatedVkTransitionTimestamp` / `eip8288AggregatedVk`, or the geth genesis
keys `eip8288AggregatedVkTimestamp` / `eip8288AggregatedVk`; the pinned backend verifies only its
own key, so proofs under any other key fail. These options are devnet-only: EIP-8288 treats
`AGGREGATED_VK` as fixed at the protocol level, and no production network overrides it.

## eth/73 proof wrapper propagation

When the EIP-8288 fork is active, the node advertises the provisional `eth/73` of
[EIP-8437](https://github.com/ethereum/EIPs/pull/12423), which extends `eth/72` with three
messages at the provisional IDs `0x16`–`0x18`: NewProofWrapperHashes, GetProofWrappers and
ProofWrappers. Status is unchanged and peers negotiate no verifier: each validates under its own
target fork.

The only object is the canonical EIP-8288 wrapper
`[transactions, mode, [dependencies, proof_content]]`, carrying each transaction's full pooled
encoding, at most `MAX_WRAPPER_BYTES` (2 MiB).
An announcement entry is `[mode, length, hash]`; a returned wrapper must match the responding
peer's announced mode and length as well as the hash. NewProofWrapperHashes and GetProofWrappers
are bounded before decompression at their largest valid sizes, 2,499 and 1,072 bytes. Requests to a
peer set `compact = 1` once this node caches transaction encodings that peer sent in full within
validated wrappers (LRU, 4 MiB per peer and 64 MiB overall, released with the connection); a
responder then replaces each encoding it already sent in full on that connection with its
`[encoding-hash]` where smaller. References are resolved from the cache and the complete wrapper must
match its announced hash and length; a reference to an evicted encoding discards the item and
retries it with `compact = 0` without penalty, while a reference in a `compact = 0` response or a
reconstruction past the announced length disconnects the peer. Responses
carry whole wrappers under the 2 MiB `eth` soft response limit, always including the first one.
A wrapper is announced to every `eth/73` peer once validated, except a hash already announced or
sent on that connection, or one whose transactions the peer's exchanged wrappers already cover
without more proof bytes or verification work. No wrapper is announced, served, admitted or
reaggregated before full validation.

Dependency-bearing transactions travel only inside their covering wrappers: on every `eth`
version they are neither broadcast nor announced, and GetPooledTransactions answers them as
unavailable. The one exception is EIP-8070 blob availability on `eth/73`: a blob-carrying one is
wrapped with its blob data elided (commitments and cell proofs only), and once its wrapper validates
it waits, with its proof coverage pinned, for its cells. Cells announced for it before then are
recorded but not fetched; after validation they are fetched and checked like type-3 cells, the pool
admits the transaction, and its cells are announced through NewPooledTransactionHashes. A wrapper
submitted with full blob data, as over RPC, is admitted directly and relayed at once with its blob
data elided (only blobs with cell proofs can be). Produced wrappers carry such transactions in the
same elided form, and block production includes them like
type-3 blob transactions. On `eth/73` every blob-carrying frame transaction uses EIP-8070 cells: one
without dependencies is announced with a cell mask, served elided through PooledTransactions and
fetched like a type-3 one, though its cells are requested only once its body is known; on `eth/72`
it still travels whole.

Local policy follows the EIP-8437 recommended defaults: 30-second requests, at most three fetch
sources per wrapper, 8 MiB/s served per peer, four announcements per second per peer and
1,024 pending announcements, at most a quarter per peer, kept for 60 seconds. Received wrappers are
charged to per-peer and global byte budgets before allocation. Proof failures never penalize the
serving peer, including across a fork that changes the verifier; an invalid wrapper is not fetched
again while the verifier that judged it stays current.

Every EIP-8288 `AGGREGATION_INTERVAL` (one second), a tracked aggregation worker produces mode-1
wrappers over bounded subsets until every pending dependency-bearing transaction is covered or
one interval of proving has elapsed; the next cycle resumes the rotation where it stopped. Ticks
coalesce while proving. Selections rotate across disjoint groups, reuse authenticated child proofs and discard
removed dependencies. Completed selections are checked against current pool membership
before publication. `eth_getProofWrapper` returns a copy without invoking the prover.
Managed aggregation folds at most four direct witnesses or two children per call.
Cancellation is checked between calls; an individual native call remains uninterruptible.

The node-wide prover decorator shares up to 64 verified aggregation steps within 32 MiB, keyed by
immutable complete inputs, verification key and expected statement. Identical concurrent requests
reuse completed work; cached hits and verification do not wait for unrelated proving. New background
work yields to active or waiting production requests. Waiting requests observe cancellation within
bounded polling intervals and check again before starting native work; an active native call remains
noninterruptible. Producing processors also retain a bounded local cache and consult the shared
exact dependency-set cache after execution determines the final body. Work completed after an
improvement deadline remains reusable, while the expired caller still cancels before publishing a
block. This cache is a bounded optimization: a folding working set larger than its capacity can
still repeat work, so the backend's 256-dependency acceptance limit does not guarantee production
within a normal slot budget.

## Proof-bearing inclusion lists

A signed inclusion list carries `recursive_stark = InclusionListProof{stark_proof, proof_deps}`
inline, whose serialized SSZ container fits `MAX_IL_PROOF_BYTES`; an empty proof is `"0x"` with no
`proof_deps`. The proof is often a mempool wrapper's and may cover a superset of the list's
dependencies. Under the target block's fork, a nonempty proof whose `proof_deps` are sorted,
deduplicated and leanSPHINCS-only is verified against `get_deps_hash(proof_deps)` and
`AGGREGATED_VK`, its verdict cached by `(keccak256(stark_proof), deps_hash, AGGREGATED_VK)`.
Each entry binds independently: a dependency-free one always, and a dependency-bearing one that
passes EIP-8141/EIP-8288 static validity when the list's proof verifies and its `proof_deps`
contain `dependencies(tx)`. The builder folds each verified list proof into its block aggregate
whole and discards the dependencies absent from `dependencies(block)`.

With both EIP-8288 and EIP-7805 active, `engine_getInclusionListV2` returns `transactions` with
`recursiveStark: {starkProof, proofDeps}`, `engine_forkchoiceUpdatedV6` accepts
`PayloadAttributesV6.inclusionLists` and `engine_newPayloadV7` receives `InclusionListV1` entries `{transactions, recursiveStark}` in
committee-position order. Earlier API versions retain their existing formats. The execution
client assumes the consensus client supplies the lists counted by EIP-7805; it does not
authenticate consensus-layer signatures.

Request bounds (list count and size, a missing or oversized proof, a `proofDeps` entry that is not
96 bytes) fail the call; an undecodable entry or an invalid proof only affects its own list.
`getPayload` does not echo inclusion-list proofs. Signing a list remains a consensus-layer
integration requirement.

The local list draws its dependency-free transactions as before, then fills the remaining
`MAX_TRANSACTIONS_BYTES_PER_INCLUSION_LIST` bytes with ready, statically valid
dependency-bearing pool transactions covered by one held recursive proof: a validated mode-1
wrapper proof or one from the background aggregation cycle, reused whole with its canonical
`proof_deps`. Candidates come from a bounded uniform sample of senders, at most one each, drawn
before any state read or validation. Taking them in random order, the proof is the smallest
held one covering a candidate whose container fits `MAX_IL_PROOF_BYTES` and that verifies,
normally from the verdict cache, under the `AGGREGATED_VK` of the fork predicted for the next
slot, so proof size or count cannot steer which transactions are listed. `engine_getInclusionListV2` never
proves, since its 1 s timeout is below a recursive aggregation, and verifies at most two held
proofs per call. Without a covering proof, or with no bytes left, the list carries no
dependency-bearing transaction and an empty proof.

Legacy inclusion sources and producers without an inclusion source use mutually exclusive
decoding fallbacks; the proof-aware source overrides that path with its prepared snapshot.

A builder merges its running wrapper aggregate with any inclusion-list proofs it does not cover,
discarding what its block does not need; EIP-8288 bounds a step's discards by the sum of its
input dependency lists and leaves the number of bounded steps to the slot deadline. The pinned
guest folds two recursive inputs per step and discards at most 256 dependencies, so the managed
aggregator merges over a binary tree, pruning each child separately, at the cost of extra proving
rounds; inclusion-list proofs whose dependency lists exceed the local 256-dependency bound
cannot be folded.

The separate [frame/FOCIL PR #13590](https://github.com/NethermindEth/nethermind/pull/13590)
adds per-includer VERIFY budgets, claimed evaluation positions and BAL replay. It overlaps
this branch's frame eligibility checks and Engine plumbing. Integration should reuse its
eligibility engine, replacing this branch's end-state prefix simulation while retaining
Lean proof admission and dependency gas accounting. This branch remains based on master;
it does not yet include that open draft.

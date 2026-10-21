# EIP-8288 native Lean integration

This adapter uses a temporary [leanVM mixed-recursion fork](https://github.com/Marchhill/leanVM/tree/cf1e92f87a7aec052b680f73e56145e866218583),
based on the Daisugi-compatible `f33f31bf7c1191667e29a68a3acae63b9164c1c6` revision.
The fork adds one recursive guest for Keccak SPHINCS claims and generic leanVM programs;
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

The C ABI is version 7, with Cdecl calls, 32-byte hashes/keys and `size_t` buffer lengths.
`nlean_limits` writes nine u32 values (proof bytes, dependencies, recursive children,
SPHINCS witness bytes, generic STARK count, instructions, operand offset, aggregation input bytes,
mixed guest proof bytes); startup
checks these against the managed protocol bounds before accepting work.
Verification/proving return 1 on success, 0 on failure and -1 for a contained panic;
ABI 7 introduced -1, so startup rejects an ABI 6 library that reports panics as 0. Callers initialize proof output
pointers to null; successful proof allocations must be released once with `nlean_free`
using the returned pointer and length. Verification contains upstream panics and never
accepts malformed inputs. Panic diagnostics on ABI callers and the pinned parallel worker
pool are suppressed while native calls are active; the previous hook remains active for
unrelated threads and outside native calls. The upstream pool resumes worker panics on the
dispatcher, where the ABI catches them.

## Prototype wire format

All framing integers are unsigned 32-bit little-endian. The native backend bounds proofs and
individual witnesses to 8 MiB and aggregation input to 18 MiB; managed callers first apply
EIP-8288's 1.25 MiB `MAX_AGGREGATE_PROOF_BYTES` to every aggregate proof. This is an input-buffer limit,
not a process memory bound. Dependency lists are bounded to 256; recursive children and distinct generic
STARK claims to 16 each. This format and the
pinned key are prototype protocol choices, pending finalized EIP-8288 encodings.

The serialized mixed guest proof is the whole `stark_proof`; an empty dependency list has an
empty `stark_proof`. Blocks, wrappers and inclusion-list proofs reject a `stark_proof` above
`MAX_AGGREGATE_PROOF_BYTES` before decoding or verification; measured proofs of this guest are
265–300 KB. Standalone generic witnesses may be up to the native 8 MiB bound and are verified
before recursive coverage can replace them. These bounds are not process memory guarantees.

The current EIP-8288 companion places `stark_proof` in the block body and commits to
`keccak256(stark_proof)` and `block_deps_hash` as two flat 32-byte header fields. Header synchronization therefore
uses compact commitments. `eth` `BlockBodies` carry `stark_proof` after the existing fields from
the activating fork on every `eth` version, as for withdrawals. Block decoding and validation
enforce `MAX_AGGREGATE_PROOF_BYTES`, the proof hash, and the exact dependency union, verifying
under the block fork's `AGGREGATED_VK`. Engine payloads and payload-body responses
carry a single `starkProof` field; the execution layer derives the dependency commitment from
the payload transactions. An empty dependency union requires an empty proof.
The decoded block cache skips proofs above 64 KiB. Invalid-block diagnostics retain large
proof-bearing records in a separate eight-entry / 64 MiB tier, counting body proofs and
returning owned copies.

A zero dependency `data_hash` resolves to the containing frame transaction's canonical
signature hash before sorting and deduplication, for every scheme, so a zero leanSPHINCS
message or leanSTARK `pub_input_hash` cannot be declared. Raw frame bytes remain zero for
signing and `FRAMEDATACOPY`; wrapper, inclusion-list and block proof statements carry the
resolved dependency.

* **SPHINCS witness:** public key (32 bytes), signature (6176 bytes). As in EIP-8288, the
  dependency key is the public key itself, root then public parameter. Both the key and the
  signature over `data_hash` are checked.
* **leanSTARK witness:** bytecode blob length, canonical bytecode, then fixed-integer bincode
  serialization of the upstream CPU proof (no trailing bytes). The dependency key is
  Keccak-256 of the EIP-8288 domain tag followed by that canonical verification-key encoding. `data_hash` is the VM's 32-byte public commitment,
  packed into two 128-bit field cells; the guest must constrain the relation it represents.
  At most 2048 instructions and operand offsets through 65535 are accepted; the offset
  limit bounds upstream assembly's g-power table. Each instruction is a 45-byte record: opcode
  byte, four u32 operands, three u64 immediate limbs, then u32 BLAKE2s metadata operand.
  Opcodes 0–6 are XOR, MUL, SET, DEREF, JUMP, BLAKE2s, SHA3. Unused fields are zero. SHA3 packs its four extra message offsets
  in the first two immediate limbs, capacity/output offsets in the third, and its digest flag
  in the metadata field.
* **aggregation input:** direct count then `(96-byte dependency, witness blob)` pairs;
  child count then `(dependency count/triples, proof blob)` pairs; discard count/triples.
* **aggregate:** the EIP-8288 `stark_proof`, one mixed guest proof without dependencies,
  generic programs or raw witnesses. It verifies against `deps_hash` and `AGGREGATED_VK`
  alone; a child's dependencies come from the aggregation input. Proving an empty list
  returns an empty `stark_proof`, which callers check without the backend.

The recursive key is the actual mixed guest's Fiat-Shamir seed, pinned in
`Eip8288Constants.AggregatedVk`. The guest authenticates signature claims, generic program
commitments and public inputs, verifies child mixed proofs, and applies union, deduplication
and discard selection. The adapter requires exact canonical EIP dependency triples and
recomputes their `get_deps_hash`; caller metadata cannot create an unproved claim.
Every supplied raw witness and child proof is authenticated even when another input already
covers the same claim. An unchanged verified statement can reuse its parent proof without
executing another recursive proving step.

`get_deps_hash` is BLAKE2s-256 over the canonical dependencies' full 96-byte
`scheme || data_hash || verification_key` entries, so the same data and key under another
scheme is another dependency. The guest's `enabled_schemes` are leanSPHINCS (`0x10`) and
leanSTARK (`0x11`), matching `Eip8288Constants.LeanStarkSchemeEnabled`; it checks every entry
of the selected list and of each child list, discarded entries included. Its leanSTARK proof
format is a generic leanVM CPU proof whose `verification_key_hash` is Keccak-256 of the ASCII tag
`ethereum/8288/leanSTARK/vk/v1`, one terminal zero byte (30 bytes total), and the canonical program encoding.

This profile is incompatible with the earlier ABI 5 `NLR3` profile, its Keccak commitment and
guest key, with older `NLR2` captures, and with the 64-byte-entry ABI 6 guest, whose key differs.
Startup checks ABI 7, the new key and all nine bounds
together. New devnets require fresh chain and database namespaces.

The latest companion specification has no block dependency quota: blocks are bounded by gas, and mode-1 wrappers by a scheme-independent 4,096-dependency ceiling. This prototype guest's 256-dependency and 16-generic-claim limits are local; this key cannot prove every set the draft permits, so this prototype is not full-capacity conformance. Wrappers above the local limits are refused locally, not judged invalid, and local inability to prove or retain a set is not an inclusion-list omission excuse. Full activation requires a verifier and proof format supporting every set the fork's gas rules permit, with the complete construction, including recursive merges and discards, benchmarked against the slot deadline.

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
against available bytes and the selected guest profile. Generic admission uses the same
supported recursion geometry as proving, so a standalone-valid witness that cannot enter
the mixed guest is rejected before pool coverage is retained. Program and decoding bounds
are prototype acceptance choices beyond the unrestricted EIP; interoperating nodes must
share the pinned guest, format and bounds.

The mixed profile uses PCS inverse-rate log 1. Generic CPU proofs require committed-polynomial
log size at most 22 and bus log size at most 22; mixed child proofs permit 27 and 26 respectively.
The host and guest enforce these geometry bounds, in addition to instruction, dependency and
serialized-byte limits. A valid unrestricted upstream proof outside this profile is not admitted.

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

* `eth_sendProofWrapper("0x<RLP>")`: verify a mode 0 or mode 1 wrapper, retain its witnesses,
  and submit its transactions. It returns transaction hashes. Hash-only entries must already
  resolve in the local pool.
* `eth_getProofWrapper()`: return a hexadecimal RLP mode 1 wrapper for proof-backed pending
  transactions. Selection rotates across the pool when wrapper limits prevent including all
  candidates; unchanged selections reuse an already verified aggregate.

A raw dependency transaction without retained valid witnesses is rejected. Builders snapshot
selected witnesses, discard dependencies outside the selected transaction set, and verify the
produced block proof. Selection reserves proof gas from both execution and state gas budgets.
Managed proving folds at most four direct witnesses or two recursive children per native call;
4 MiB is the direct-leaf batching target, not a block-wide witness limit. Native inputs are bounded
to 18 MiB and output proofs to `MAX_AGGREGATE_PROOF_BYTES`. Each encoded dependency list and the selected
output are bounded to 256 dependencies; inputs may jointly cover more claims when
the discarded subset brings the output within that bound.
A verified recursive source covers duplicate generic claims without retaining or selecting
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

Wrappers are validated under their target fork, the fork active at `head.timestamp + L`, where
`L = (head.timestamp - parent.timestamp) / (head.slot_number - parent.slot_number)` is the local
EIP-7843 slot-length estimate in integer seconds (the head's fork without a parent or when slot
numbers do not increase). The target fork
fixes the verifier: `AGGREGATED_VK` for mode-1 proofs and each scheme's parameters for
mode-0 witnesses. Two bounded verdict caches hold direct-witness verdicts keyed by
`(dep, keccak256(witness), scheme_params)` and aggregate verdicts keyed by
`(keccak256(stark_proof), deps_hash, AGGREGATED_VK)`; a lookup consults only the cache of the
operation it would run. When the head reaches a target fork whose verifier differs, every held
aggregate proof and mode-1 wrapper is dropped, mode-0 witnesses are kept only if they re-validate,
pending transactions left without coverage are evicted, and nothing validated under the previous
verifier is announced or served. Development chains schedule such a fork with the chainspec
parameters `eip8288AggregatedVkTransitionTimestamp` / `eip8288AggregatedVk`, or the geth genesis
keys `eip8288AggregatedVkTimestamp` / `eip8288AggregatedVk`; the pinned backend verifies only its
own key, so proofs under any other key fail.

## eth/73 proof wrapper propagation

When the EIP-8288 fork is active, the node advertises the provisional `eth/73` of
[EIP-8437](https://github.com/ethereum/EIPs/pull/12423), which extends `eth/72` with three
messages at the provisional IDs `0x16`–`0x18`: NewProofWrapperHashes, GetProofWrappers and
ProofWrappers. Status is unchanged and peers negotiate no verifier: each validates under its own
target fork.

The only object is the canonical hash-only EIP-8288 wrapper
`[transaction_hashes, mode, [dependencies, proof_content]]`, at most `MAX_WRAPPER_BYTES` (2 MiB).
An announcement entry is `[mode, length, hash]`; a returned wrapper must match the responding
peer's announced mode and length as well as the hash. NewProofWrapperHashes and GetProofWrappers
are bounded before decompression at their largest valid sizes, 2,499 and 2,127 bytes. Responses
carry whole wrappers under the 2 MiB `eth` soft response limit, always including the first one.
A wrapper is announced to every `eth/73` peer once validated, except a hash already announced or
sent on that connection, or one whose transactions the peer's exchanged wrappers already cover
without more proof bytes or verification work. No wrapper is announced, served, admitted or
reaggregated before full validation.

Dependency-bearing transactions propagate on `eth/73` through Transactions and
NewPooledTransactionHashes alongside their covering wrappers; below `eth/73` they are neither
broadcast nor announced, and GetPooledTransactions answers them as unavailable. An envelope
received before its covering wrapper is retained for 60 seconds within byte, count and per-peer
limits and stands in for recovery, but its transaction is admitted only with the validated
wrapper. One that expires with no covering wrapper on its connection counts against the sender's
gossip score, unless the sender's announcements are unresolved or a local limit intervened.
Missing envelopes are recovered with GetPooledTransactions from peers that announced or served
the wrapper, checked against any type and size the responding peer announced. A holder serves
every listed envelope, including ones evicted from its pool, from a bounded wrapper-envelope cache.

Local policy follows the EIP-8437 recommended defaults: 30-second requests, 120-second recovery
across at most three sources, 8 MiB/s served per peer, four announcements per second per peer and
1,024 pending announcements, at most a quarter per peer, kept for 60 seconds. Received wrappers and recovered envelopes are
charged to per-peer and global byte budgets before allocation. Around a fork that changes the
verifier, a peer's proof failure is a local refusal from when the local target fork switches to it,
or the fork after the target fork does, until the head is `Network.ProofWrapperForkGraceBlocks`
(default 64) blocks past the fork.

The EIP-8288 one-second tick refreshes pending selections independently of one tracked
aggregation worker. Ticks coalesce while proving; they do not imply one-second proving.
Selections rotate across disjoint groups, reuse authenticated child proofs and discard
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

A signed inclusion list commits to `recursive_stark = [stark_proof_hash, deps_hash]`, or null.
The committed proof usually is a mempool wrapper's, which other nodes already hold, and may
cover a superset of the list's dependencies; otherwise it travels as an
`il_proof = [stark_proof, proof_deps]` on the consensus layer. A node holds a valid proof for a
commitment only with all three of the proof bytes, their `proof_deps` and a `VALID` aggregate
verdict under the target block fork's `AGGREGATED_VK`, and `proof_deps` contain the list's `deps`,
the union over entries that decode and pass EIP-8141/EIP-8288 static validity. A verdict whose
bytes were released only spares verifying the proof again when it is supplied again.

The methods follow [execution-apis#945](https://github.com/ethereum/execution-apis/pull/945). Each
takes the target block's `timestamp`, whose fork fixes static validity and the key, and fails with
`-38005` outside EIP-8288:

* `engine_verifyInclusionListProofV1(timestamp, transactions, recursiveStark, ilProof|null,
  attestationDeadlineMs)` returns `{status, validationError}`: `VALID` when the proof's bytes are
  held, a lookup, or the supplied matching `il_proof` matches a cached `VALID` verdict or verifies,
  retaining the bytes until `attestationDeadlineMs`; `INVALID` when the list has no dependencies,
  the known `proof_deps` omit one, an `INVALID` verdict is held (with or without an `il_proof`), or
  the matching proof fails; `PROOF_UNKNOWN` otherwise, including for a `VALID` verdict whose bytes
  were released. After parameter decoding, a matching `il_proof` failing submit's shape checks is
  `INVALID` without verification, its verdict recorded; one that does not hash to the commitment
  is ignored, whatever its shape.
* `engine_getInclusionListProofV1(timestamp, starkProofHash)` returns the held `il_proof`, from
  a validated mode-1 wrapper or a retained `il_proof`, or null once its bytes are released.
* `engine_submitInclusionListProofV1(timestamp, ilProof, viewFreezeDeadlineMs)` verifies a
  proof-topic `il_proof` and records its verdict, `VALID` or `INVALID`, retaining a valid proof
  until `viewFreezeDeadlineMs`; size bounds, noncanonical `proof_deps` and schemes outside
  `ENABLED_SCHEMES` are `INVALID` without verification, and that verdict is recorded too.

Retention follows the deadlines the consensus layer supplies, in Unix milliseconds: a proof stays
until the latest deadline any call gave it, and a deadline already past adds none. The execution
client derives no deadline from slot lengths. The store is bounded by EIP-8288's peak of 80
`il_proof`s (130 MiB).

With both EIP-8288 and EIP-7805 active, `engine_forkchoiceUpdatedV6` accepts
`PayloadAttributesV6.inclusionLists` and `engine_newPayloadV7` receives separate
`InclusionListV1` entries in strictly increasing committee-position order. Each entry carries
`committeeIndex`, `transactions`, and `recursiveStark: {starkProofHash, depsHash}` or null.
Earlier API versions retain their existing formats. The execution client assumes the consensus
client supplies the lists counted by EIP-7805; it does not authenticate consensus-layer
signatures.

A counted list always obligates its dependency-free entries, those without a dependency frame; its
dependency-bearing entries, including one whose dependency frame carries no triples, only when this
node holds a valid proof for its commitment. A commitment to an unknown or failing
proof, one whose `proof_deps` omit a list dependency, or one on a list without dependencies is
treated as null. Request bounds (positions, sizes, an incomplete commitment) fail the call; an
undecodable entry only affects its own list. Builders fold each held committed proof into the
block aggregate whole and discard the dependencies their block does not need, so the block proof
stays exact. `getPayload` does not echo inclusion-list commitments.

`engine_getInclusionListV1` keeps its existing non-frame sampling and returns transaction
bytes only; constructing and signing a list with its proof remains a consensus-layer
integration requirement.

Legacy inclusion sources and producers without an inclusion source use mutually exclusive
decoding fallbacks; the proof-aware source overrides that path with its prepared snapshot.

A builder merges its running wrapper aggregate with any inclusion-list proofs it does not cover,
discarding what its block does not need; EIP-8288 bounds a step's discards by the sum of its
input dependency lists and leaves the number of bounded steps to the slot deadline. The pinned
guest folds two recursive inputs per step and discards at most 256 dependencies, so the managed
aggregator merges over a binary tree, pruning each child separately, at the cost of extra proving
rounds; the cold case of 16 unheld 4,096-dependency wrapper proofs exceeds what it can prove.

The separate [frame/FOCIL PR #13590](https://github.com/NethermindEth/nethermind/pull/13590)
adds per-includer VERIFY budgets, claimed evaluation positions and BAL replay. It overlaps
this branch's frame eligibility checks and Engine plumbing. Integration should reuse its
eligibility engine, replacing this branch's end-state prefix simulation while retaining
Lean proof admission and dependency gas accounting. This branch remains based on master;
it does not yet include that open draft.

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

The C ABI is version 6, with Cdecl calls, 32-byte hashes/keys and `size_t` buffer lengths.
`nlean_limits` writes nine u32 values (proof bytes, dependencies, recursive children,
SPHINCS witness bytes, generic STARK count, instructions, operand offset, aggregation input bytes,
mixed guest proof bytes); startup
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
not a process memory bound. Dependency lists are bounded to 256; recursive children and distinct generic
STARK claims to 16 each. This format and the
pinned key are prototype protocol choices, pending finalized EIP-8288 encodings.

The serialized mixed guest proof is the whole `stark_proof`, bounded to 8 MiB; an empty
dependency list has an empty `stark_proof`. Verification and proving enforce the same bounds;
standalone generic witnesses may be up to 8 MiB and are verified before recursive coverage
can replace them. These are prototype consensus limits, not proven maxima of upstream
proof sizes or process memory guarantees. Larger proofs fail closed.

The current EIP-8288 companion places `stark_proof` in the block body and commits to
`[keccak256(stark_proof), block_deps_hash]` in the header. Header synchronization therefore
uses compact commitments. Block decoding and validation enforce the 8 MiB proof ceiling,
the proof hash, and the exact dependency union. Engine payloads and payload-body responses
carry `recursiveStarkProof`; an empty dependency union requires an empty proof.
The decoded block cache skips proofs above 64 KiB. Invalid-block diagnostics retain large
proof-bearing records in a separate eight-entry / 64 MiB tier, counting body proofs and
inclusion-list proof/dependency buffers and returning owned copies.

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
of the selected list and of each child list, discarded entries included. Its leanSTARK profile
is a generic leanVM CPU proof whose `verification_key_hash` is Keccak-256 of the ASCII tag
`ethereum/8288/leanSTARK/vk/v1`, one terminal zero byte (30 bytes total), and the canonical program encoding.

This profile is incompatible with the earlier ABI 5 `NLR3` profile, its Keccak commitment and
guest key, with older `NLR2` captures, and with the 64-byte-entry ABI 6 guest, whose key differs.
Startup checks ABI 6, the new key and all nine bounds
together; lean status checks the pinned key before proof transfer. New devnets require fresh
chain and database namespaces.

The latest companion specification uses a 4,096-dependency block ceiling and a shared 4,096-unit budget (leanSPHINCS weight 1, leanSTARK weight 16). These are distinct from this prototype guest's 256-dependency and 16-generic-claim limits. This key cannot prove every set allowed by the draft's public capacity; this prototype is not full-capacity conformance. Local inability to prove or retain a set is not an inclusion-list omission excuse. Full activation requires a profile supporting every valid public set. A trivial-program timing sample cannot justify weight 16 for every permitted generic program; worst-case profile geometry, recursive merges and discards still require measurement.

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
* `eth_sendProofInclusionList("0x<RLP>")`: verify a self-contained proof-bearing inclusion
  list and submit its transactions. Consensus-layer inclusion-list obligations are transported
  through the Engine payload attributes, rather than being created by this RPC call.

A raw dependency transaction without retained valid witnesses is rejected. Builders snapshot
selected witnesses, discard dependencies outside the selected transaction set, and verify the
produced header proof. Selection reserves proof gas from both execution and state gas budgets.
Managed proving folds at most four direct witnesses or two recursive children per native call;
4 MiB is the direct-leaf batching target, not a block-wide witness limit. Native inputs are bounded
to 18 MiB and output proofs to 8 MiB. Each encoded dependency list and the selected
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
peer ingress retries temporary contention for a bounded interval. Inclusion-list package
submission through RPC validates a complete pool-admission package. Consensus eligibility instead filters
entries individually, preserving ordinary and zero-dependency entries when proof coverage fails.
The Network project references the shared Consensus admission service so RPC and negotiated
proof gossip use the same verification, pool insertion and witness-retention rules.

## Negotiated proof gossip

When the EIP-8288 fork is active, the node advertises `lean/1` alongside ETH. The
[EIP-8437 draft](https://github.com/ethereum/EIPs/pull/12423) defines eight RLP messages:
Status, AnnounceObjects, GetObjects, Objects, GetChunks, Chunk, Complete and Cancel.
Status negotiates chain ID, genesis, profiles, supported kinds and maximum object size.
The profile is `keccak256("lean/1/profile\0" || AGGREGATED_VK)`.

Kind 1 carries the canonical EIP-8288 wrapper
`[transaction_hashes, mode, [dependencies, proof_content]]`. Hashes are sorted and unique;
transaction envelopes are recovered over the same authenticated peer's ETH protocol,
including envelopes retained with validated wrappers after pool eviction. The descriptor
is `[kind, profile_id, context, byte_length, content_hash]`; its object ID is
`keccak256("lean/1/object\0" || rlp(descriptor))`.

Kind 2 is optional block-proof retrieval: context is `[block_hash]`, and the body is
`[stark_proof]`. The ordinary header must already be known. The received proof must match
its hash commitment and verify against its dependency commitment before use. Ordinary
block bodies already carry the proof. There is no inclusion-list object kind.
An ordinary ETH body response counts proof bytes toward its soft 2 MiB response budget;
the first body is still returned when its proof alone is larger, up to the 8 MiB proof bound.
The existing 16 MiB decoded-packet and 12 MiB compressed-frame ceilings still apply. A body
whose proof and transactions exceed those transport ceilings remains unsupported; optional
kind-2 retrieval does not permit stripping the proof from an ordinary block body.

Objects are bounded to 9 MiB, chunks to 64 KiB and messages to 128 KiB. A request may
ask for at most 32 chunks (2 MiB), with four outstanding requests per peer in each
direction. Request IDs are positive and strictly increasing across both request types;
gaps are permitted. Cancelled requests retain their slots until termination or expiry.

One source supplies an assembly at a time. Changing sources discards its bytes, and at
most three source attempts are allowed within the original five-minute deadline.
Requests expire after 30 seconds without progress or two minutes absolutely; assemblies
expire after 30 seconds without progress or five minutes absolutely. Deferred work is
bounded and scheduled fairly. Quota pressure defers work without penalizing the peer.

The shared receive budget is 18 MiB per source and 72 MiB globally. It includes assembly
storage, recovered envelopes, completion/verification ownership and transport reservations.
The contiguous object allocation is reserved on its first accepted chunk and remains
charged through validation. A separate 128 MiB / 256-object store retains validated
objects and their envelopes. Objects are served or announced only after validation.

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

During channel backpressure, the shared sender retains up to 64 non-bulk messages
within 12 MiB and drains them before bulk writes; exceeding the control queue closes the
entire RLPx session, including its ETH and SNAP protocols, rather than silently dropping
control responses. This policy applies to sessions with Lean bulk transport enabled.
Control codecs run independently of bulk serialization, preserving their
synchronous message ownership. Control responses wait for the current
chunk's compressed frame to drain. The RLPx merger still accepts one fragmented context
at a time; application chunks are complete independent messages rather than interleaved
fragments of one RLPx object.

## Proof-bearing inclusion lists

With both EIP-8288 and EIP-7805 active, `engine_forkchoiceUpdatedV6` accepts
`PayloadAttributesV6.inclusionLists` and `engine_newPayloadV7` receives separate
`InclusionListV1` entries in strictly increasing committee-position order. Each entry carries
`committeeIndex`, `transactions`, and nullable `recursiveStark: {starkProof, depsHash}`.
Earlier API versions retain their existing formats. The execution client assumes that the
consensus client supplies the authenticated lists counted by EIP-7805; it does not authenticate
consensus-layer signatures. Proof-bearing FOCIL RLP is `[transactions, [stark_proof, deps_hash]]`.

Each list's `depsHash` must equal the commitment to its decoded transactions' exact dependency
union, and its proof must verify against that statement. An empty dependency union carries an
empty proof without a verifier call. Malformed frames are ineligible. Dependency-free
obligations remain when that list's proof is missing or invalid; a valid independent list can
still require the same dependency-bearing transaction. Builders and validators use the same
per-list eligibility rules. Each build owns decoded snapshots and verified parent proofs even
when shared witness storage is full. `getPayload` does not echo inclusion-list proofs; the
consensus client supplies the authenticated lists to `newPayload`.

Signed consensus-layer inclusion-list gossip and an agreed construction API returning the
proof for signing remain integration requirements. `engine_getInclusionListV1` keeps its
existing non-frame sampling and returns transaction bytes only; it does not construct a
proof-bearing list. These missing CL/construction pieces prevent claiming complete activation
of the latest companion's inclusion-list protocol.

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

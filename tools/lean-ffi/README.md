# EIP-8288 native Lean integration

This library pins official [leanVM](https://github.com/leanEthereum/leanVM/tree/b977f5fa8f07cb2d40cbd76a66deefd67d975c30)
commit `b977f5fa8f07cb2d40cbd76a66deefd67d975c30`. It verifies real BLAKE2s SPHINCS
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

The C ABI is version 3, with Cdecl calls, 32-byte hashes/keys and `size_t` buffer lengths.
`nlean_limits` writes seven u32 values (bytes, dependencies, recursive children,
SPHINCS witness bytes, generic STARK count, instructions, operand offset); startup
checks these against the managed protocol bounds before accepting work.
Verification/proving return 1 on success and 0 on failure. Callers initialize proof output
pointers to null; successful proof allocations must be released once with `nlean_free`
using the returned pointer and length. Verification contains upstream panics and never
accepts malformed inputs. ABI-thread panic diagnostics are suppressed while failures
return 0; the previous Rust panic hook remains active outside ABI calls.

## Prototype wire format

All framing integers are unsigned 32-bit little-endian. Proofs and aggregation inputs are
bounded to 8 MiB; dependency lists to 4096; recursive children and carried generic
STARKs to 16 each. This format and the
pinned key are prototype protocol choices, pending finalized EIP-8288 encodings.

The [EIP](https://eips.ethereum.org/EIPS/eip-8288#recursive-stark-header-entry) requires the proof in the block header, so the header database and caches retain it.
Generic STARK witnesses can make each header approach 8 MiB. On proof-bearing chains,
header serving loads full headers incrementally and stops at a 9 MiB response budget
before loading the next header. Headers with proofs above 64 KiB bypass the header
cache. Large proofs reduce batch size. This trades header-sync throughput and storage for the prototype's
current header format; a finalized sidecar format would require a protocol change.

* **SPHINCS witness:** public key (32 bytes), signature (4924 bytes). The dependency key
  is Keccak-256 of the public key. Both this hash and the signature over `data_hash` are checked.
* **leanSTARK witness:** bytecode blob length, canonical bytecode, then fixed-integer bincode
  serialization of the upstream CPU proof (no trailing bytes). The dependency key is
  Keccak-256 of that canonical bytecode. `data_hash` is the VM's 32-byte public commitment,
  packed into two 128-bit field cells; the guest must constrain the relation it represents.
  At most 16384 instructions and operand offsets through 65535 are accepted; the offset
  limit bounds upstream assembly's g-power table. Each instruction is a 45-byte record: opcode
  byte, four u32 operands, three u64 immediate limbs, then u32 BLAKE2s metadata operand.
  Opcodes 0–5 are XOR, MUL, SET, DEREF, JUMP, BLAKE2s. Unused fields are zero.
* **aggregation input:** direct count then `(96-byte dependency, witness blob)` pairs;
  child count then `(dependency count/triples, proof blob)` pairs; discard count/triples.
* **aggregate:** `NLR2`, canonical dependency count/triples, upstream EthereumProof blob
  (empty when there are no SPHINCS claims), generic-STARK count then `(dependency, witness blob)`
  pairs. A blob is its length followed by bytes.

The recursive key is the actual upstream guest's Fiat-Shamir seed, pinned in
`Eip8288Constants.AggregatedVk`. Root verification compares full dependency triples against
cryptographically verified SPHINCS claims, verifies every generic STARK, and recomputes the
canonical Keccak dependency commitment. Proving verifies every direct witness and child,
then applies union, deduplication and discard selection. Caller-supplied dependency metadata
cannot create a claim absent from the verified witnesses.

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
selected witnesses, recursively fold batches of at most sixteen children, discard dependencies
outside the selected transaction set, and verify the produced header proof. Selection reserves
recursive-proof gas from both execution and state gas budgets. Witness storage is bounded to
64 MiB; production reserves at most 4 MiB of serialized witnesses per block to stay within the
native 8 MiB input and proof bounds and the 4096-dependency envelope limit. Production also
bounds witness coverage, including dependencies it discards, to 4096. Transactions beyond the current witness budget remain
pending for another block.

## Negotiated proof gossip

When the prototype fork is active at the node's head, it advertises `lean/1` and
`lean/2` alongside normal Ethereum capabilities. Peers choose their highest common
version; `lean/1` remains a whole-wrapper fallback. The existing protocol registry
shares negotiation and shutdown with Ethereum handlers, using the Consensus proof
admission service and background scheduler.

Message 0 is a 72-byte status: chain ID (u64 big-endian), genesis hash (32 bytes), pinned
recursive guest key (32 bytes). All three must match before message 1 is accepted;
a mismatch disables only lean, preserving the session's other protocols.
The RLP mempool wrapper includes full transactions and is bounded by 10 MiB and
4096 transactions. Outgoing selection reserves 8 MiB for the proof before encoding
transactions. `lean/1` sends the whole wrapper within the 16 MiB frame cap.

`lean/2` streams independent chunks: a 48-byte header holds the whole-wrapper
Keccak commitment and big-endian total length, index, count and chunk size, followed
by chunk bytes. The default chunk is 64 KiB, with 16/32/64/128 KiB supported and
128 KiB as the maximum. Geometry is checked before allocation. The commitment is
unauthenticated transport integrity; the complete wrapper's proof and transactions
still pass shared admission before gaining proof-backed pool coverage. This is an
[EIP-8411](https://eips.ethereum.org/EIPS/eip-8411)-inspired bounded transfer, without
signed bids or Merkle authentication of individual chunks.

Both protocol versions share a 64 MiB / 64-object receive budget covering incomplete
reassembly and completed wrappers queued or undergoing verification. `lean/2` retains
at most two objects per peer (up to 20 MiB). `lean/1` has one active and one pending
wrapper; decoding a replacement can temporarily add a third copy before replacing
the pending wrapper, with that copy also charged to the global budget.
Incomplete objects expire after 30 seconds without a new chunk, or five minutes
absolutely; duplicates do not extend their lifetime, and timer expiry needs no new
inbound traffic. Queue replacement and shutdown release leases. A 20 MiB/s per-peer
receive budget is checked before copying. Each verification returns pending work to
the shared scheduler. Invalid proofs or encodings disconnect the peer; ordinary pool
rejection does not.

One node-wide worker aggregates eligible pending transactions every second. Bounded
selection rotates past the last selected transaction; unchanged dependency sets reuse
verified proofs. Each peer keeps one active transfer and the latest pending selection;
a cadence never cancels an active object. Chunk sends await actual channel writes and
yield between chunks so control and ETH traffic can interleave. A whole transfer is
memoized only after every write succeeds. Unchanged wrappers refresh every 30–35 seconds
with per-peer jitter, recovering dropped queued work without admission acknowledgements.
Generic STARK witnesses remain carried and verified. Each peer remembers at most 64
recent delivered hashes. Only admitted transactions retain witness coverage.
`eth_getProofWrapper` returns a copy of the background-produced wrapper without invoking
the prover. Cancellation is checked between native calls; shutdown cancels peer work
and joins the worker.

During channel backpressure, the shared sender retains up to 64 non-bulk messages
within 12 MiB and drains them before bulk writes. Control responses wait for the current
chunk's compressed frame to drain. The RLPx merger still accepts one fragmented context
at a time; application chunks are complete independent messages rather than interleaved
fragments of one RLPx object.

## Proof-bearing inclusion lists

With both EIP-8288 and EIP-7805 active, FCUv5 payload attributes and newPayloadV6's
execution payload accept `inclusionListRecursiveStark: {starkProof, blockDepsHash}`.
It is required when the inclusion list declares dependencies. The proof commits to
that list's canonical sorted/deduplicated dependencies and is verified before mandatory
prefix checks; its verified witnesses seed the builder's shared proof store. An invalid
or missing package proof creates no mandatory transaction obligations, consistently
for builders and validators. `getPayload` does not echo this proof; the consensus client
supplies its own inclusion list and proof to `newPayload`. The consensus
client can construct it through the same native aggregation or proof-wrapper path.
`engine_getInclusionListV1` retains its existing non-frame candidate sampling; externally
formed proof-bearing frame inclusion lists are validated and enforced.

The separate [frame/FOCIL PR #13590](https://github.com/NethermindEth/nethermind/pull/13590)
adds per-includer VERIFY budgets, claimed evaluation positions and BAL replay. It overlaps
this branch's frame eligibility checks and Engine plumbing. Integration should reuse its
eligibility engine, replacing this branch's end-state prefix simulation while retaining
Lean proof admission and dependency gas accounting. This branch remains based on master;
it does not yet include that open draft.

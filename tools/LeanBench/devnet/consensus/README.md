This creates a fresh Kurtosis `lean8288-mixed` network: two execution nodes, two Lighthouse beacon nodes, 32 validators and Dora. It uses Amsterdam/Gloas at genesis, activates the EIP-8288 prototype, and leaves Bogota/Heze disabled. The funded test sender is private key 2 on chain ID 10088289.
This is the current mixed-recursion profile: native ABI 5, a newly pinned mixed guest key,
and one opaque recursive proof covering SPHINCS and generic STARK claims. The candidate
profile accepts at most 256 dependencies, 16 distinct generic claims and 2048 instructions
per generic program; these acceptance bounds do not guarantee proving RAM or slot latency. Build Runner,
native library, fixtures and helper from the same verified revision; the generated manifest
records the loaded ABI/key and acceptance bounds checked at startup. Do not reuse the
previous `lean8288` databases, proof cache, genesis or validator artifacts. Its ABI 4 / NLR2
captures remain historical evidence for that earlier carried-generic profile.

Prepare in a new local/remote directory such as `/root/eip8288-mixed-devnet`, with a newly
cloned package checkout, fresh `client` publish and freshly generated genesis. The old
enclave and viewer remain stopped; these instructions do not authorize restarting them.
Wait for the genuine guest key and complete native validation before running the commands.

The devnet Engine relay shares a bounded proof cache between both beacon nodes. It preserves `recursiveStarkProof` and `recursiveStarkBlockDepsHash` across Lighthouse's payload encoding, keyed by the real block hash. It forwards every Engine verdict unchanged and rejects a proof cache miss. This is a local interoperability bridge, not CL support for the prototype's additional consensus fields or proof-bearing inclusion lists. Each listener permits two active requests through response writes, independently of the other listener. The relay rejects Bogota `newPayloadV6`; this setup is Amsterdam-only. Cache files persist within the relay container; recreating the enclave creates a fresh chain.

Before launching services, generate only the three preproved requests below from the final
pinned backend. Run each command sequentially with all execution/beacon services stopped;
fixture preparation performs real proving and must not compete with the live nodes.

```sh
export LEANVM_NUM_THREADS=1
export RUSTFLAGS='-C target-cpu=native'
dotnet build tools/LeanBench/LeanBench.csproj -c Release --disable-build-servers -m:1 -p:BuildLeanFfi=true
(cd tools/lean-ffi && cargo build --release --locked --example bench_fixtures)
tools/lean-ffi/target/release/examples/bench_fixtures /root/eip8288-mixed-devnet/fixtures 2 2
dotnet tools/artifacts/bin/LeanBench/release/LeanBench.dll \
  --devnet-transactions=true --fixtures=/root/eip8288-mixed-devnet/fixtures \
  --chain-id=10088289 --cases=mixed1 --wrapper-modes=recursive --rounds=1 \
  --fixture-offset=0 --nonce=0 --out=/root/eip8288-mixed-devnet/requests/stage1
dotnet tools/artifacts/bin/LeanBench/release/LeanBench.dll \
  --devnet-transactions=true --fixtures=/root/eip8288-mixed-devnet/fixtures \
  --chain-id=10088289 --cases=sphincs1 --wrapper-modes=recursive --rounds=1 \
  --fixture-offset=1 --nonce=1 --out=/root/eip8288-mixed-devnet/requests/stage2-sphincs
dotnet tools/artifacts/bin/LeanBench/release/LeanBench.dll \
  --devnet-transactions=true --fixtures=/root/eip8288-mixed-devnet/fixtures \
  --chain-id=10088289 --cases=stark1 --wrapper-modes=recursive --rounds=1 \
  --fixture-offset=1 --nonce=2 --out=/root/eip8288-mixed-devnet/requests/stage2-generic
```

These commands run from the coherent child checkout after merging the verified main
revision. Do not use the helper's default direct-wrapper cases for the initial live test.
The candidate profile accepts inverse-rate log 1 only; the older rate-2 duplicate-witness
benchmark is outside it.

Stage 1 submits only its nonce-0 recursive wrapper to execution node 1 after both nodes
have matching heads, negotiated `lean/1` peers and empty pools. The one transaction needs
exactly its parent root's two claims, so both producers reuse that verified statement;
peer wrapper admission and block import verify it without fresh generic proving. Check
successful receipts on both nodes, the header dependency commitment and beacon anchors,
then wait for finality before continuing.

Stage 2 is a separate, conditional aggregation test: submit the nonce-1 SPHINCS parent
and nonce-2 generic parent to node 1 while otherwise idle. Their fresh claims require a
new mixed root if included together. Gossip and independent block building can cause
both execution nodes to prove concurrently; one worker per process does not serialize
work across nodes. Start this stage only after measuring the final backend on Linux and
checking both the per-node memory margin and combined host margin. Never submit raw
generic wrappers or a sixteen-generic batch to this bounded startup test.

Prepare an isolated checkout of `ethpandaops/ethereum-package`, pin its revision, copy `../engine_proxy.py` to `ethereum-package/lean-engine-proxy.py`, and run:

```sh
python3 patch_package.py ethereum-package --capture-engine-payloads
cp -a /path/to/published/client ./client
docker build -f Runner.Dockerfile -t nethermind:lean8288-mixed-devnet .
docker build -f Genesis.Dockerfile -t ethpandaops/ethereum-genesis-generator:lean8288-mixed .
kurtosis run --dry-run --enclave lean8288-mixed --args-file params.yaml ethereum-package
kurtosis run --enclave lean8288-mixed --args-file params.yaml --parallelism 1 ethereum-package
```

`Runner.Dockerfile` defaults to the same pinned .NET 10 runtime image as the repository's Dockerfile and copies the published client, including the native Lean library. Override `--build-arg BASE_IMAGE=<compatible-runtime-image>` only when needed for the published runtime/architecture. The checked-in args file contains no host address; for remote port publication, set `port_publisher.nat_exit_ip` to your own reachable host before running Kurtosis. The genesis wrapper uses the official generator's EL stage, adds prototype activation and the funded account, then generates a matching beacon genesis. JWT and validator secrets are generated by Kurtosis and remain in enclave artifacts.

The package patch routes both CL Engine connections through one authenticated relay and allocates these distinct host ports:

`--capture-engine-payloads` is optional. It saves the exact proof-restored `engine_newPayloadV5` JSON request before forwarding, including the BAL, execution requests and beacon root, in the relay's private `/proof-cache/engine-captures/<blockhash>.json` directory. Both listeners share a first-record-per-block archive limited to 16 records, 64 MiB total and 32 MiB per record; empty 12-byte NLR3 proofs are skipped. Capture errors leave Engine verdicts unchanged. Files have mode `0600`, contain no HTTP authorization headers, and must remain outside Git and the public viewer. Capture does not imply payload validity: use the native helper's original-proof and canonical-hash inspection on an owned copy before mutation testing.

| Service | Node 1 | Node 2 |
|---|---:|---:|
| Execution JSON-RPC | 19445 | 19545 |
| Execution Engine | 19551 | 19651 |
| Execution P2P | 19403 | 19503 |
| Beacon REST API | 19552 | 19652 |

Dora listens on port 19490. After explicitly launching the fresh enclave, check real beacon progress at `/eth/v1/beacon/headers/head` and `/eth/v1/beacon/states/head/finality_checkpoints`; the archived Engine-driver dashboard on 19480 represented a separate network and is stopped.

The configuration caps each execution node at two CPU cores and 5120 MB, each beacon node at one core and 1536 MB, and each validator client at half a core and 768 MB. These are memory limits, not reservations; the combined limits, Dora and the relay can exceed a 15 GiB host. Kurtosis expresses these values in decimal MB. The recovered test containers were instead updated to exactly 5 GiB each, without additional swap.

In the archived ABI 4 run, the initial 2048 MB execution limit caused an OOM kill during an honest two-transaction SPHINCS run. Archived native-only measurements reached approximately 1.375 GB for two direct claims, 2.223 GB for four direct claims and 3.450 GB for binary recursive aggregation, before managed-client memory. Those measurements are not Linux peak-memory guarantees. Count and serialized-input bounds do not bound proving RAM, and cancellation cannot interrupt an active native call.

The new mixed guest has no measured host-memory guarantee from those earlier runs. The 5120 MB execution cap is only a candidate for the preproved verification/reuse stage and a separately measured small-parent merge; it cannot accommodate a large raw generic prover plus managed-client memory. Measure the exact Linux profile and fresh Runner baseline before launching the conditional aggregation stage. Keep the captured proof and runtime hashes alongside the resource samples; measurements from a different profile or host do not establish this margin. The load observer stops further submissions when host available memory falls below 6 GiB, an execution node reaches 80% of its limit, or host CPU reaches 85%; it also stops on an OOM, restart or consensus-health failure. Sampling can miss short allocation spikes, and stopping admissions does not cancel work already queued. Preserve OOM evidence before recovery, drain accepted transactions and confirm both execution heads and beacon finality before increasing load. Do not submit large generic-proof batches merely to fill the network.

# Two-Runner proof devnet

`prepare.py` generates isolated Runner configs. `drive.py` is a small Python Engine API driver, not a consensus client. It alternates the producer between two processes, submits each genuine signed wrapper only to the other process, waits for the transaction to arrive through negotiated `lean/1`, then imports the proof-bearing block into both nodes and compares successful receipts and canonical heads. Tampered wrapper witnesses are rejected before each valid submission. The optional offline mutator reconstructs the header hash after corrupting the proof or dependency commitment, so negative block-import checks exercise the proof validation path instead of merely failing the header hash check.

Generate configuration from the checked-out Amsterdam chain template:

```sh
python3 tools/LeanBench/devnet/prepare.py \
  --out=/tmp/lean-devnet-config \
  --runtime-root=/root/eip8288-devnet-20261003/runtime
```

Copy `chain.json`, `node1.json` and `node2.json` into that runtime directory. Generate a shared 32-byte JWT secret **on the Runner host**, with restrictive permissions, then start two Runner processes using `--config <runtime>/node1.json` and `--config <runtime>/node2.json`. The RPC/Engine ports are 19145/19151 and 19245/19251; P2P ports are 19303/19304. All listeners bind to localhost. Each node has its own database, keystore and identity. The driver adds the other node as a static peer using `admin_addPeer`.

```sh
umask 077
openssl rand -hex 32 > /root/eip8288-devnet-20261003/runtime/jwt.hex
```

The shared genesis activates Amsterdam plus EIP-8141/8288/8250/8272/7906 at timestamp zero, starts after the Merge with terminal total difficulty zero, and funds the empty account for test key scalar 2 with 100 ETH. This key is public test data. The master default VERIFY implementation authenticates its secp256k1 frame signature. Both binaries must load the same genuine native ABI/backend and guest key. Keep the generated genesis timestamp and files identical; use fresh databases. The default chain omits Bogota/FOCIL. `--bogota` on **both** configuration generator and driver activates its Engine versions; an empty inclusion list does not test FOCIL enforcement.

Build the child LeanBench tool and native library, then generate genuine requests (fixtures must match the pinned backend):

```sh
dotnet build tools/LeanBench/LeanBench.csproj -c Release -p:BuildLeanFfi=true
(cd tools/lean-ffi && cargo build --release --locked --example bench_fixtures)
tools/lean-ffi/target/release/examples/bench_fixtures /tmp/lean-devnet-fixtures 64 4
dotnet tools/artifacts/bin/LeanBench/release/LeanBench.dll \
  --devnet-transactions=true --fixtures=/tmp/lean-devnet-fixtures \
  --out=/root/eip8288-devnet-20261003/runtime/transactions
python3 tools/LeanBench/devnet/drive.py \
  --manifest=/root/eip8288-devnet-20261003/runtime/transactions/manifest.json \
  --jwt=/root/eip8288-devnet-20261003/runtime/jwt.hex \
  --out=/root/eip8288-devnet-20261003/runtime/driver \
  --mutator='dotnet tools/artifacts/bin/LeanBench/release/LeanBench.dll'
```

The generator defaults to eight consecutive nonce transactions: SPHINCS 1, generic STARK 1, mixed 4, and SPHINCS 16, each with direct and recursive wrapper admission. Generic proofs are genuinely verified and carried, not recursively compressed. The driver uses FCU V4/getPayload V6/newPayload V5 for Amsterdam, or FCU V5/getPayload V6/newPayload V6 for Bogota. Amsterdam attributes include slot number and target gas limit. `--build-wait` defaults to 10 seconds and only delays the driver's getPayload request. It does **not** extend proving time. Merge improvements have a separate budget, `Blocks.SecondsPerSlot × Blocks.SingleBlockImprovementOfSlot`: the normal 12-second slot and 0.25 fraction allow 3 seconds. `Blocks.BlockProductionTimeoutMs` does not replace this budget. getPayload cancels an unfinished improvement and may return the initial empty payload. Payloads also become eligible for cache cleanup after three slots (36 seconds here); the default cleanup timer runs every six slots (72 seconds here), so this is an eligibility threshold rather than an exact expiry time. Waiting substantially longer can return `UnknownPayload`. Timings include the deliberate driver wait and are functional evidence, not benchmark measurements.

`report.json` is updated atomically after each block; payload requests, receipts and offline mutation inspections are saved alongside it. Use `--source-revision=<exact Runner commit>` to identify the deployed source. The report records manifest and driver SHA-256 hashes, client versions, capabilities, gossip arrival, proof bytes, dependency commitment, producer and import results. It also checks that stale timestamp attributes are rejected without changing the final head. Without `--mutator`, the report explicitly records that negative block-proof checks were not run. A successful run demonstrates Engine-driven production/import and peer wrapper transport; it does not establish public-network interoperability or full consensus-client operation.

For a larger signature block after the default eight-block run, generate four **separate valid 16-signature transactions**, then run the driver with `--resume --transactions-per-block=4`. The generator command uses `--nonce=8 --rounds=4 --cases=sphincs16 --wrapper-modes=direct`. A completed run would check all four transactions, all eight node receipts, and the native inspector's exact 64 distinct signature / zero generic dependency count. Keep generic STARK cases at one proof per block for this load plan. The report adds `transactionCount` and `transactionHashes`; `transactionHash` retains the first entry for simple dashboards. Use a separate manifest/output directory for this continuation. Peer gossip transports valid 16-signature wrappers; proof-bearing block import uses the Engine API and does not imply chunked block-proof sidecars.

For a committed capture, retain the compact driver report, receipt/inspection summaries and SHA-256 hashes of the source manifest, wrappers, block proofs and complete payload files. Keep full payload requests and fixture binaries in the runtime directory or another untracked artifact location. Reproduction commands, Runner/helper source revisions and binary/backend hashes should identify the run; these functional checks and deliberately delayed build timings are not throughput benchmarks.

The [standalone capture](captures/2026-10-03/README.md) records eight successful blocks under source `5fae5c6e8c4bb6df6b825e8cc970b8e306052784`, followed by **two unsuccessful 64-signature attempts**. The normal-budget attempt returned a transaction set different from the four gossiped transactions after its improvement was cancelled. An explicitly extended 120-second improvement budget plus a 90-second driver wait instead returned `UnknownPayload` after cache cleanup. Neither attempt demonstrates 64-signature block inclusion. The batch has 64 distinct dependencies internally, with overlap against earlier fixtures and possible cached coverage reuse; it is not a fresh 64-signature proving benchmark. These captures describe that deployed revision, not later improvements.

Export only compact evidence on the Runner host, then copy the JSON output to the capture directory:

```sh
python3 tools/LeanBench/devnet/capture.py \
  --runtime-root=/root/eip8288-devnet-20261003/runtime > /tmp/standalone-evidence.json
```

The exporter reads fixed driver/manifest paths, summarizes receipts, preserves inspection results, and hashes complete requests and proof bytes. It never opens JWT, configs, private keys or fixture binaries. It performs no RPC requests or proving. The separate [Kurtosis setup](consensus/README.md) uses real beacon/validator processes with a local Engine proof-field relay; its results must be captured separately rather than added to this Engine-driver run.

## Run and view

After preparing the shared genesis/configs and generating the JWT on the Runner host, start the two published clients and retain their fixed log names:

```sh
mkdir -p /root/eip8288-devnet-20261003/logs
dotnet /path/to/client/nethermind.dll --config /root/eip8288-devnet-20261003/runtime/node1.json > /root/eip8288-devnet-20261003/logs/node1.log 2>&1 &
dotnet /path/to/client/nethermind.dll --config /root/eip8288-devnet-20261003/runtime/node2.json > /root/eip8288-devnet-20261003/logs/node2.log 2>&1 &
```

Run the signed-manifest driver command above, then start the read-only viewer:

```sh
python3 tools/LeanBench/devnet/status.py --root=/root/eip8288-devnet-20261003 --port=19480
```

Open `http://127.0.0.1:19480/` on that host, or `http://<runner-host>:19480/` after explicitly starting the viewer with `--bind=0.0.0.0` and allowing access to that port. The viewer uses only the fixed `runtime/driver/report.json` and `runtime/driver-sphincs64/report.json` reports, local node RPCs, and `logs/{node1,node2,driver,driver-sphincs64}.log`. It exposes `/` and `/api/status`, with no arbitrary file browsing, configuration, JWT or private-key output. Expected invalid-proof rejections are labelled as successful checks; actual failed runs remain visible separately. The separate live-beacon network is viewed in Dora as described in the consensus guide.

Offline helper and real localhost response-ownership checks:

```sh
python3 -m unittest discover -s tools/LeanBench/devnet -p 'test_*.py'
```

Bounded real-CL load:

`spam.py` uses the existing Kurtosis nodes; it never supplies fork choice. Deploy it together with its sibling `drive.py` RPC helper. Prepare fixtures before starting the timed driver:

```sh
dotnet tools/artifacts/bin/LeanBench/release/LeanBench.dll --devnet-transactions=true \
  --fixtures=/path/to/fixtures --fixture-offset=64 --nonce=3 --rounds=4 \
  --cases=sphincs1 --wrapper-modes=direct,recursive --out=/path/to/stage1-fixtures
python3 tools/LeanBench/devnet/spam.py --manifest=/path/to/stage1-fixtures/manifest.json \
  --out=/path/to/stage1-report --rate=0.05 --limit=8 --workers=2 \
  --health-file=/path/to/spam-health.json --proof-metadata=/path/to/spam-proof-metadata.json \
  --dependency-inspector='dotnet /path/to/LeanBench.dll' --source-revision='<actual deployed base and patch>'
```

The sampler files must come from the actual host and proof relay cache. Optional Runner RSS high-water marks are lifetime process peaks, not isolated-stage or native-only measurements. Resource collection errors or stale metrics stop further admissions; observational reads retry transient transport failures at most three times within five seconds for RPC or eight seconds for beacon REST, with bounded endpoint/latency diagnostics, while OOM/restart evidence stops retries immediately. Submissions are never automatically retried. A busy negative probe stops a fresh stage as capacity backpressure before offering its valid wrapper; it does not pass cryptographic verification. Primary and subsequent stop events are retained separately. `--observe-report=<failed report>` uses a separate output directory to recover receipt/proof/finality observations without new admissions or throughput attribution. Active requests remain bounded and their outcomes are saved. Each fresh transaction uses a distinct dependency across both wrapper modes. The driver checks tampered RPC ingress, actual frame dependency counts and the included union, cached proof bytes/hash, the Core Keccak commitment, both receipts, nonoptimistic Gloas beacon execution bids and eventual finality. At finality it rechecks the canonical beacon bids and EL block hashes; inclusion and completed finality are separate report fields. Receipt drain defaults to 180 seconds and finality observation to 1200 seconds (at most 1800); 12-second slots and 32-slot epochs can require roughly two to three epochs, particularly after an outage. Sender nonce equality is checked at a new stage's receipt drain; historical recovery permits later nonce advancement. A later stage may start while the prior passive finality observer continues. Preparation and finality are excluded from receipt goodput; the report distinguishes the arrival window, observed valid-wrapper RPC-start spacing and goodput including drain. Historical frozen `f600` captures used pre-negative-probe cycle starts for the interarrival metric; that value is labelled as probe-cycle spacing and is not rewritten. Receipt goodput remains the original authenticated count divided by its recorded arrival/drain window. Gossip seen after a receipt is explicitly distinguished from pending-pool gossip.

Start conservatively with four transactions at 0.025 tx/s, then four at 0.05 tx/s and eight at 0.1 tx/s only after each healthy drain. The optional `--limit` selects a prefix of the signed manifest; subsequent subsets must retain the exact signed wire requests and advance the sender nonce and fixture metadata accordingly. Stop fresh-load escalation on backpressure or a failed health/drain check. Any later rate increase requires a healthy completed stage; these are exploratory load points, not maximum-capacity claims. A separately labelled `--shared-dependency=true` fixture stage with `--allow-shared-dependency` in the driver uses one fresh dependency on its first admission, then reuses it to exercise transaction transport/pool/EVM; it does not measure fresh-signature capacity. Invalid-only bursts use `--invalid-only`, at most 8 workers and 64 total attempts. `proofRejected` and `boundedBusy` outcomes are separate; busy responses do not count as cryptographic verification. Invalid-only stages may reuse negative templates whose transaction is already mined: known-before/after checks prevent new leakage, and sender nonces must remain unchanged. Repeated templates may hit the rejection cache; attempts and templates are reported separately, not as native verification counts. This driver permits SPHINCS-only claims; the separate functional generic case remains limited to one generic proof per block.

[Captured bounded load and mobile graphs](captures/2026-10-03/spam/README.md) retain failed runs, authenticated receipt goodput and separate finality recovery. The shared-dependency point is transport/pool/EVM throughput, not fresh-signature capacity.

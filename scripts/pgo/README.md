# PGO collection, publishing and validation

Profiles are generated artifacts, not checked-in binaries. A successful collection
requires both Fusaka payload processing and the private `eth_call` corpus, from
the same image and source revision. Each workload runs twice:

| Capture | Runtime configuration | Profile data |
| --- | --- | --- |
| Instrumentation | TieredPGO enabled, ReadyToRun disabled | Edge counts and type/method histograms |
| Sampling | TieredCompilation and TieredPGO disabled, ReadyToRun disabled | Managed sampled caller/callee weights and SPGO block counts |

Sampling uses one native code version per method because the pinned converter's
native-to-IL map is keyed by method/re-JIT ID rather than tier. Both traces start
with the process and finalize on graceful shutdown. Do not substitute the existing
GC/contention EventPipe sidecars: those do not collect all the required events.
Training timings are not performance measurements.

## Why the previous runtime profile was ineffective

In [.NET 10.0.11's PGO manager](https://github.com/dotnet/runtime/blob/79d0c463f1b55624c874a11585f7e47731e8d675/src/coreclr/vm/pgo.cpp),
`PgoManager::ReadPgoData` returns immediately when TieredPGO is enabled.
Shipping `.jit.gz` and setting `DOTNET_ReadPGOData=1` therefore did not seed this
client's dynamic PGO. Disabling dynamic PGO would lose adaptation to actual traffic.
Use MIBC for crossgen2/NativeAOT and keep dynamic PGO enabled in the client.

The standard and chiseled images now explicitly publish ReadyToRun. With a supplied
MIBC they embed instrumentation data, enable hot/cold splitting and use
Pettis–Hansen layout. Supplying the additional callchain JSON selects CallFrequency
instead. Cross-module optimization is limited to Docker publishing, which pins the
framework reference packs to the runtime image version. The Dockerfile updater
updates that reference-pack version alongside the images.

## Toolchain

`build-tool.sh` builds dotnet-pgo from .NET 10.0.11 commit
`79d0c463f1b55624c874a11585f7e47731e8d675`. Prerequisites: git, Python 3.11+, and
the runtime repository's managed-build prerequisites. The collection workflow
uses the existing Linux benchmark runner and installs PyYAML through `uv`.

`patch-tool.py` applies small, exact-match patches to that pinned source:

- Keep native-to-IL mapping events during the **initial** EventPipe-to-ETLX
  conversion. Setting `KeepAllEvents` only when reopening the ETLX is too late.
- Feed managed `SampleProfiler` events to the existing sampled-call-graph builder
  and SPGO correlator, only with `--spgo`. Unmanaged samples are excluded.
- Correct the sample time-window predicate and apply it to SPGO as well.
- Fix the CallWeights JSON writer, which otherwise writes named properties
  directly inside an array (`dotnet/runtime#125935`).

This uses stock CoreCLR EventPipe rather than the earlier PR's custom
LTTng-enabled CoreCLR/perfcollect conversion. It is managed sampling, not hardware
LBR sampling. It does not measure native RocksDB/GC CPU work. The converter reports
unattributed samples and flow-smoothing diagnostics; retain those logs when
assessing coverage. Flow-smoothing exceptions fail conversion, rather than being
silently swallowed.

This is not evidence that managed sampling replaces perfcollect without a loss
in profile quality. Perfcollect-generated MIBCs remain valid compiler inputs.
Compare both on the same source and workload before choosing a collection method;
positive counters and successful compilation alone are insufficient.

The converter preserves the full typed graph in MIBC. `CallChainWriter.cs` also
exports CallFrequency JSON. That format cannot identify overloads or generic
instantiations reliably, so the export includes only unambiguous, module-qualified names and reports how
many edges it excluded. Compare CallFrequency against the more complete MIBC-based
Pettis–Hansen layout before selecting it for a release.

## Collect on a benchmark runner or a provisioned local Linux host

Build the collection image from the checkout being profiled:

```sh
docker build -f Dockerfile.pgo --build-arg COMMIT_HASH="$(git rev-parse HEAD)" -t nethermind-pgo:collect .
bash scripts/pgo/build-tool.sh /tmp/nethermind-pgo-runtime
for mode in instrumentation sampling; do
  uv run --with pyyaml==6.0.2 python scripts/pgo/collect.py fusaka --mode "$mode" \
    --image nethermind-pgo:collect --output "/large-disk/pgo/fusaka-$mode" \
    --expb-config /mnt/sda/expb-data/github-action-mainnet-fusaka-flat.yaml --amount 1000
  uv run --with pyyaml==6.0.2 python scripts/pgo/collect.py ethcall --mode "$mode" \
    --image nethermind-pgo:collect --output "/large-disk/pgo/ethcall-$mode" \
    --db-source /mnt/sda/nethermind-flat-25490000 \
    --corpus /mnt/sda/expb-data/rpc-bench/eth-call-corpus-20260805T104605Z-497-safe.jsonl.gz
done
python3 scripts/pgo/convert.py \
  --tool /tmp/nethermind-pgo-runtime/artifacts/bin/coreclr/linux.x64.Release/dotnet-pgo/dotnet-pgo.dll \
  --output /large-disk/pgo/profile \
  /large-disk/pgo/fusaka-instrumentation /large-disk/pgo/fusaka-sampling \
  /large-disk/pgo/ethcall-instrumentation /large-disk/pgo/ethcall-sampling
```

Install EXPB at the revision pinned in `collect-pgo-profile.yml` first. Existing
snapshot isolation and teardown checks are reused for RPC; collection outputs
must be new directories on a disk with enough room. The RPC replay uses the same
`corpus_parity.py` workload as the existing GitHub RPC runs, at 100 rps for 25
complete corpus passes. It is a training replay, not the k6 latency comparison.

The workflow is manual, builds the collection image from its commit on the runner
unless an immutable image is supplied, and uploads only
MIBC, callchain JSON and provenance. It neither commits binaries nor opens a PR.
Raw traces, private requests, responses and node logs are not uploaded. The
manifest records image/source identity, workload and trace checksums, and per-capture coverage.
Mixed-image, missing-workload, empty-instrumentation and zero-SPGO/call-graph
collections are rejected. Keep generated profiles with their manifest.

## Publish

Copy the generated profile and callchain into the ignored Runner `pgo` directory:

```sh
mkdir -p src/Nethermind/Nethermind.Runner/pgo
cp /large-disk/pgo/profile/nethermind.mibc src/Nethermind/Nethermind.Runner/pgo/
cp /large-disk/pgo/profile/nethermind.callchain.json src/Nethermind/Nethermind.Runner/pgo/
docker build -t nethermind-pgo:candidate \
  --build-arg PGO_PROFILE=/nethermind/src/Nethermind/Nethermind.Runner/pgo/nethermind.mibc \
  --build-arg PGO_PROFILE_SHA256="$(jq -r .profile_sha256 /large-disk/pgo/profile/manifest.json)" \
  --build-arg PGO_CALLCHAIN=/nethermind/src/Nethermind/Nethermind.Runner/pgo/nethermind.callchain.json \
  --build-arg PGO_CALLCHAIN_SHA256="$(jq -r .callchain_sha256 /large-disk/pgo/profile/manifest.json)" .
```

Use `-f Dockerfile.chiseled` for the shellless image. Omit the two callchain
arguments to test Pettis–Hansen. Omit all profile arguments for an R2R baseline;
also pass `--build-arg PUBLISH_READY_TO_RUN=false` for the original IL baseline.
Missing, empty or checksum-mismatched requested artifacts fail the build.
Compiler binlogs remain in the build stage at `/tmp/publish.binlog`.

Outside Docker, publish with an absolute `-p:NethermindPgoProfile=/path/profile.mibc`
and `-p:PublishReadyToRun=true`; optionally supply
`-p:NethermindPgoCallChain=/path/nethermind.callchain.json`. Cross-module inlining is
not enabled by default for deployments whose runtime can change independently.

For a NativeAOT-compatible project, the same profile property with
`-p:PublishAot=true` supplies `MibcFile` to ILC. This does not make the full
reflection/plugin-based Nethermind Runner NativeAOT-compatible.

The ZisK guest uses bflat directly, not MSBuild's ILC target:

```sh
make -C src/Nethermind/Nethermind.Stateless.ZiskGuest build \
  PGO_PROFILE=/absolute/path/nethermind.mibc
make -C src/Nethermind/Nethermind.Stateless.ZiskGuest run INPUT=25532382.ssz
```

`PGO_PROFILE` mounts the MIBC read-only and passes `--mibc` to **bflat compilation**.
The existing ISA and embedded-resource gates remain enabled. Profiles collected
on CoreCLR are not representative of guest costs by themselves: compare an
unprofiled guest and a profiled guest on the same block, check the complete public
output and success flag, and record `ziskemu -X --save-stats` costs. Emulator steps
and weighted costs are not GPU proving times.

## Validation before claiming an improvement

- `python3 -m unittest discover -s scripts/pgo -p 'test_*.py' -v` checks profile gates.
- `tools/PgoSmoke` is a deterministic compiler integration probe. Train its `Add`
  path, generate MIBC, publish with R2R and NativeAOT, then run both its default
  path and `cold` path. The latter uses a different interface implementation to
  check the speculative optimization's fallback. Check the compiler response
  files/binlog for the actual profile argument.
- `inspect-r2r.py <assemblies...>` requires native entry points and an embedded
  PGO section. It reports cross-module inline data and hot/cold maps. Check at
  least `Nethermind.Evm.dll` and `Nethermind.JsonRpc.dll` in the final image.
- `smoke-rpc.py --url http://127.0.0.1:8545` checks 10,000 exact `eth_call` results
  on a disposable node, using arithmetic, storage and calldata overrides.
- Use the existing Fusaka EXPB runs for block validity/state roots and unprofiled
  timing comparisons; use reserved payloads beyond the training range as well.
- Use `run-rpc-benchmarks.yml` / `jsonbench-sweep` for full private-corpus response
  parity and latency. Compare IL, R2R without application PGO, and R2R with PGO on
  the same runner/snapshot. Repeat with candidate order reversed. Use the existing
  60-second warmup and 100-rps/120-second measured cells; keep cold-start results
  separate. Do not compare instrumentation/sampling runs against production runs.

Profile presence and successful compilation do not establish a speedup. Record
image/profile hashes, all errors, correctness results and repeated A/B timings.

For the pinned ZisK emulator, retain `output.bin` and `stats.csv` from
`ziskemu --steps -X --save-stats /n/stats.csv -o /n/output.bin ...` in each arm's
directory. Check the full public output against an independently known result,
then reject a candidate whose weighted cost increases:

```sh
python3 scripts/pgo/check_guest.py --baseline /results/unprofiled \
  --expected-output "$EXPECTED_PUBLIC_OUTPUT_HEX" /results/profiled
```

The command emits the measurements as JSON and exits nonzero for wrong/truncated
output, missing counters or a cost regression. A lower step count does not
override an increased weighted cost. Keep rejected results in the comparison.

### Historical profiles as comparison controls

The profile at the head of [#10877](https://github.com/NethermindEth/nethermind/pull/10877)
was replaced after the perfcollect results described in its body. Use immutable
revisions and inspect the actual data instead of assuming its latest MIBC still
contains SPGO:

| Profile revision | Actual MIBC contents |
| --- | --- |
| `2f370884f8e14be6cf868bbd3d762a6d3d3e643f` | Later mainnet instrumentation; no SPGO blocks or call weights |
| `2ffe6b514c2e201d84769f84726a5d0844f2fbdb` | Earlier perfcollect-containing profile; 1,380 SPGO methods and 12,385 call edges |

Both are at `src/Nethermind/Nethermind.Runner/pgo/nethermind.mibc` in those
commits. Extract them into an ignored local directory after fetching the PR's
history; do not silently substitute either for a fresh collection or commit them.
The existing `NethermindPgoProfile` and guest `PGO_PROFILE` inputs accept either.

On current-source guest block 25,532,382, the later mainnet profile reduced weighted
cost by 0.9485%, the earlier perfcollect-containing profile increased it by 0.5050%,
and their MIBC merge reduced it by 0.6844%, relative to an unprofiled guest. All
three public outputs matched. The baseline and later-mainnet result repeated
exactly in reverse order. The synthetic RPC combined profile increased cost by
0.5574%. These are one-block results, not release-profile selection or proof that
one collector is better: source revisions and training workloads differ.

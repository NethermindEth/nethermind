# Aggregation scheduling checks

This mode measures Nethermind's scheduling decisions using the production gossip,
wrapper service, proof store and recursive aggregator. A controlled backend returns
statement-checked synthetic proof bytes. It does not load Lean, prove signatures,
measure native performance, or start services. One row admits a real secp256k1
signed FrameTx to the production pool; its dependency proof backend remains
controlled and synthetic.

```sh
dotnet build tools/LeanBench/LeanBench.csproj -c Release --disable-build-servers -m:1
dotnet tools/artifacts/bin/LeanBench/release/LeanBench.dll \
  --scheduling-checks=true --out=/tmp/lean-scheduling-before \
  --source-revision=RECORDED_SOURCE_REVISION
```

After merging the scheduling change, rebuild and run with another output directory
and `--require-fresh=true`. That switch requires a transaction removed during
proving to remain undelivered when the old proof finishes, and requires the
actual block processor to reuse the background proof without another prove.
It also requires a completed eligible wrapper to remain deliverable while
a new selection is blocked in proving.
The default records this behavior so an earlier scheduler can be measured without disguising it as
an optimized result.

Each JSON capture records its command, production source label, reported hashes
of the two overlaid harness files, and actual tool/network/consensus assembly
SHA256 hashes. Assembly hashes identify a particular build: SourceLink, checkout
paths and build flags can change them even when the source is identical. Use the
Git revision plus the two source hashes to identify source content. Keep before
and after captures separate.

The scenarios check:

- 120 unchanged selections, then a changed transaction body sharing the same
  dependency: count actual backend proving calls and require no new dependency
  proof for the changed body.
- 120 exact-parent builder attempts: require zero new proving calls, retain
  verification on reuse, and reject a damaged parent.
- A real secp256k1 signed FrameTx admitted to the production pool: build its
  background wrapper, then build two actual blocks through the production
  block processor. Count additional proving calls and validate the proof
  statement against the selected block body. Mutate the first returned proof
  to check retained proof ownership on the second improvement pass. Dependency
  verification remains synthetic in this scheduling scenario.
- One controlled blocked proof with 1, 3 or 10 one-second virtual timer
  notifications: count overlapping calls, completions and deliveries. Require
  one active prover and no repeated proving of the unchanged statement. Advance
  beyond the bounded delivery memo and require a refresh without another proof.
- Remove the selected transaction while its proof is blocked; record whether
  completing old work broadcasts the removed transaction.
- Prebuild wrapper A, retain A's transaction, add transaction B and block the
  new A+B proof. Fire one-second virtual ticks and record whether completed A
  is delivered while A+B is incomplete. The controlled peer declines the
  first cached-A offer regardless of when it executes, so a late AddPeer callback
  cannot count as cadence progress. Only subsequent offers while the new proof
  remains blocked can signal delivery. After three virtual tick notifications,
  await an actual completed-A delivery event for at most five wall-clock seconds.
  Offer additional one-second virtual retry ticks at 100ms wall intervals while
  waiting. Hold the proof gate until the event or timeout; record elapsed
  observation time and offered notifications. Virtual retry counts can therefore
  differ between runs using this identical event-driven policy. A baseline zero means no delivery was observed in
  that finite window, not an unbounded absence claim. Require zero incomplete
  deliveries and exactly one A+B delivery after release.

`virtualBusySeconds` and timer counts describe the manually advanced clock.
The `sendsWhileBlocked` values in the coalescing/removal rows are informational
counter snapshots after offered ticks and brief yields; their elapsed observation
windows are recorded. They are not delivery deadlines or capacity measurements.
The separate completed-wrapper row awaits a delivery event with a five-second
limit; that test budget is not a production latency guarantee.

`harnessWallMilliseconds` includes fixture setup synchronization, brief yields
and teardown; it is not a native proving latency. A one-second cadence does not
imply that native proofs finish within one second. These checks also do not
establish sustained transaction throughput, fairness for arbitrary pool sizes,
or a bound on proof memory.

Run the production managed regression suites alongside this tool. In particular,
`LeanProofGossipTests`, `ProofWrapperServiceTests`, and
`Eip8288BlockProductionTests` exercise refresh/retry, current selection, and
verified completed proof reuse after caller cancellation. Genuine native tests
remain the cryptographic integration check; this mode is deliberately a
deterministic scheduling check.

Captured results and the count chart live in
[results/2026-10-04/scheduling](results/2026-10-04/scheduling/README.md).


## Reproduce the baseline with the identical harness

Run this from a committed benchmark checkout containing the revised files whose
hashes appear below. Resolve that checkout to an immutable commit first. Both
`SchedulingChecks.cs` **and** `Program.cs` must be overlaid: the historical
baseline lacks the scheduling dispatch, so running its original entry point
would start the full benchmark instead. The hash checks fail before building if
the chosen benchmark commit does not contain this exact harness.

```sh
set -e
benchmark_source=$(git rev-parse HEAD)
baseline_checkout=/tmp/lean-scheduling-baseline
harness_sha=dd9fce48cc9973ae7e271794c5b2862007600a5ea51af853458af53bfc505a24
dispatch_sha=211e47f4773444789bfcf4d5c4fcc169ed8655af21e9d001de03d92e40459813
git worktree add --detach "$baseline_checkout" c574cbb16a576c5121c1b0f9017920b776ab33a8
git show "$benchmark_source:tools/LeanBench/SchedulingChecks.cs" > "$baseline_checkout/tools/LeanBench/SchedulingChecks.cs"
git show "$benchmark_source:tools/LeanBench/Program.cs" > "$baseline_checkout/tools/LeanBench/Program.cs"
cd "$baseline_checkout"
shasum -a 256 -c <<EOF
$harness_sha  tools/LeanBench/SchedulingChecks.cs
$dispatch_sha  tools/LeanBench/Program.cs
EOF
dotnet build tools/LeanBench/LeanBench.csproj -c Release --disable-build-servers -m:1 -p:CheckForOverflowUnderflow=false
dotnet tools/artifacts/bin/LeanBench/release/LeanBench.dll \
  --scheduling-checks=true --out=/tmp/lean-scheduling-before \
  --source-revision=c574cbb16a576c5121c1b0f9017920b776ab33a8 \
  --harness-sha256="$harness_sha" --dispatch-sha256="$dispatch_sha"
```

Preserve the capture before removing the task-owned worktree. For the after run,
return to the same benchmark checkout, verify the same two source hashes, rebuild,
and use its immutable Git revision, a fresh output directory and
`--require-fresh=true`, passing both source-hash arguments again. No native
library, fixtures, services or Lean build are required for this mode.

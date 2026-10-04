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

Each JSON capture records its command, source label and actual tool/network/
consensus assembly SHA256 hashes. Keep before and after captures separate.

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
  is delivered while A+B is incomplete. The controlled peer declines an early
  A warmup send, becomes writable while proving is actively blocked, and counts
  only deliveries during that blocked phase. Require zero incomplete deliveries
  and exactly one A+B delivery after release.

`virtualBusySeconds` and timer counts describe the manually advanced clock.
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

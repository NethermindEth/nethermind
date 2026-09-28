# Corrections to the MAX_VERIFY_GAS campaign results

Status: **the pre-2026-09-19 devnet results are withdrawn.** Two defects in the traffic
generator, not in Nethermind, produced the reported finding. Both are fixed; re-measurement is
in progress. Do not cite the earlier numbers or the first two published reports.

---

## C1. Generator was GIL-bound on pure-Python ECDSA

**What.** `eth-keys` selects a signing backend by import probe. Without `coincurve` present it
falls back to `NativeECCBackend`: pure-Python ECDSA, ~990 signatures/s, and it does not release
the GIL, so N worker threads deliver the throughput of one.

**Why it mattered.** The signature-stuffed shape carries one signature per 2,800 gas of ceiling.
The generator's own maximum output was therefore a function of the independent variable:

| Ceiling | Sigs/tx | Generator max | Offered | Keeps up? |
|---|---|---|---|---|
| 100,000 | 35 | 28.3 tx/s | 25 tx/s | yes |
| 236,285 | 84 | 11.8 tx/s | 25 tx/s | no |
| 300,000 | 107 | 9.3 tx/s | 25 tx/s | no |
| 322,800 | 115 | 8.6 tx/s | 25 tx/s | no |
| 350,000 | 124 | 8.0 tx/s | 25 tx/s | no |
| 500,000 | 178 | 5.6 tx/s | 25 tx/s | no |

**Consequence.** The reported threshold "attack succeeds at ceiling >= 236,285" is the ceiling at
which the generator stopped keeping up. The reported rate cliff "between 5 and 10 tx/s at ceiling
322,800" is the generator's 8.6 tx/s limit. keccak-wide and groth16 read as benign because they
carry one signature each, so the generator never saturated on them.

The honest traffic's apparent slowdown (submit p50 1.2 ms -> 29 ms) is most plausibly GIL
starvation inside the generator process. Attacker and honest submitters are separate thread
pools in one interpreter.

**Fix.** `coincurve==20.0.0` in `traffic/requirements.txt`. Backend is now
`CoinCurveECCBackend`, which releases the GIL.

Measured, same shape, ceiling 322,800:

```
before:   1 thread 8.8 tx/s    8 threads 8.7 tx/s   64 threads 8.6 tx/s
after:    1 thread 185.8 tx/s  8 threads 385.0 tx/s
```

**Guard.** `runner.calibrate_generator()` now runs before any load. It builds the configured
shape for 2 s, emits `RESULT case=generator_capacity` with `build_rate_max` / `headroom`, and
aborts the scenario when headroom is below 1.5x. A run can no longer report a rate it cannot
generate.

---

## C2. Inclusion was measured with a censored deadline

**What.** Each accepted transaction spawned a waiter calling
`wait_for_receipt(timeout=seconds_per_slot * 6)` = 36 s, on the same thread pool as submission.
On timeout the transaction was recorded `included=false`.

**Evidence it was censoring, not a result.** Every run's *maximum* observed inclusion latency sat
within 3 s of the 36 s deadline (33.3 / 36.0 / 21.7 / 35.0 / 10.0 s across the five mechanism
runs). When the largest value recorded is the cutoff, the distribution is right-censored.

Prometheus disagreed with the generator: `nethermind_transaction_count` sawtoothed for the whole
run (filling to 94, draining to 1) with `evicted=0` and `frame_tx_expired=0`. Transactions were
leaving the pool and inclusion was the only exit.

Secondary effect: 64 waiters polling at 0.5 s made the measurement itself one of the heaviest
RPC clients on the node it was measuring, with no connection reuse.

**Fix.** `runner.ChainWatcher` replaces per-transaction receipt polling. It walks blocks, one
`eth_getBlockByNumber` per block regardless of how many transactions are outstanding, and matches
hashes. Inclusion latency is dated from the including block's timestamp.

- reports `outstanding` separately from "not included": a verdict from the chain and a
  measurement that ran out of time are different claims
- `--settle-slots` (default 10) keeps walking after the load stops, so a transaction admitted at
  the end of the window gets the same chance as one admitted mid-window
- emits `RESULT case=block_fill` with per-block transaction counts, which also answers the
  "are blocks empty during the attack" question directly

Offline coverage: `traffic/tests/test_watcher.py`.

---

## C3. Offered rate was reported, not delivered

**What.** The submit pool's queue is unbounded. The pacing loop enqueued at the nominal rate;
workers drained slower; `stop()` called `shutdown(wait=True)`, which finished the backlog *after*
the window closed. `achieved_rate` divided that total by the nominal window length, so it printed
the offered rate back regardless of what was delivered.

**Fix.** `shutdown(wait=True, cancel_futures=True)` drops the backlog, and the Submitter tracks
`_scheduled` against `submitted`, emitting `RESULT case=generator_shortfall` with
`delivered_rate` whenever they diverge.

---

## What survives

- The devnet harness itself: topology, client patches, cross-client activation proof, the 9
  acceptance checks.
- Cross-client EIP-8141 activation (Nethermind `eip8141TransitionTimestamp` == ethrex
  `hegotaTime` == 1789774679, identical genesis hash).
- The in-process `frame-tx-bench` per-shape cost measurements, which do not use this generator.
- The observation that attacker transactions are rejected at admission and never pool.

## What does not

- Every devnet inclusion figure.
- The ceiling threshold claim (>= 236,285).
- The rate cliff claim (between 5 and 10 tx/s).
- The "keccak-wide and groth16 are benign" contrast, which shared the same confound.
- The mechanism hypotheses built on top of those numbers, including the per-head simulation
  budget sweep. Budget 100 / 1000 / 0 differed by less than the censoring noise.

## Lesson for the harness

A load generator must measure and publish its own capacity before it measures anything else.
Both defects were invisible in the output: the generator reported the rate it was asked for, and
reported a censored distribution without marking it censored. The guards added here
(`generator_capacity`, `generator_shortfall`, `outstanding`) exist so each is loud next time.

---

## Re-measurement, ceiling 322,800 (2026-09-19)

Same client image, same topology, same shape. Only the generator changed.

| Attack rate | Honest submitted | Included | Outstanding | Incl. p50 | Incl. max | Tx/block | EL CPU |
|---|---|---|---|---|---|---|---|
| none (control) | 241 | 241 | 0 | 3.2 s | 6.2 s | 7.9 | 2% |
| 25 tx/s | 241 | 241 | 0 | 3.5 s | 6.0 s | 7.9 | 5% |
| 100 tx/s | 241 | 241 | 0 | 2.9 s | 5.9 s | 7.9 | 11% |
| 250 tx/s | aborted by the capacity guard (headroom 1.2) | | | | | | |

**No measurable effect on honest inclusion at any deliverable rate.** The withdrawn figure for
the 25 tx/s cell was 0.7% inclusion.

Corroborating signals that the new numbers are sound:

- Inclusion max is one slot (6.0 s), set by chain physics rather than by a deadline.
- `outstanding=0` everywhere: nothing left unresolved when the watcher stopped.
- EL CPU is linear in offered rate with no knee. A saturating system shows latency rising faster
  than load; attacker admission latency is flat at ~3.3 ms across a 4x rate increase.
- Attacker admission p50 fell from 38 ms to 3.6 ms for the identical transaction against the
  identical client. That 38 ms was GIL starvation inside the generator.

Implied client cost: ~31 us per secp256k1 recovery, ~3.3 ms for a 115-signature rejection.
Measured CPU runs below a single-core prediction, so recovery appears to parallelise.

### The capacity guard's first real encounter

The 250 tx/s cell was refused:

```
RESULT case=generator_capacity role=signature-stuffed ceiling=322800 offered_rate=250.0 \
       build_rate_max=297.6 headroom=1.2 sufficient=no
RESULT case=scenario_aborted reason="generator cannot build the offered rate for this shape"
```

The old harness would have reported `achieved_rate=250.0` here, exactly as it reported 25.0 while
delivering 8.6.

### Generator capacity after the fix, per ceiling

Build cost still scales with the ceiling, so a fair ceiling comparison needs one rate every
ceiling can generate. That rate is 75 tx/s.

| Ceiling | Sigs/tx | Build tx/s | Max honest offer (1.5x headroom) |
|---|---|---|---|
| 100,000 | 35 | 658.0 | 439 |
| 236,285 | 84 | 289.8 | 193 |
| 300,000 | 107 | 230.0 | 153 |
| 322,800 | 115 | 211.7 | 141 |
| 350,000 | 124 | 199.9 | 133 |
| 500,000 | 178 | 139.9 | 93 |

---

## Concentrated load, ceiling 322,800 (split traffic)

Whole flood aimed at one node, honest traffic at the other. 2.8x the per-node pressure of the
100 tx/s cell, and the highest this generator can honestly deliver to a single client.

| | Attacked node (el-1) | Observed node (el-2) |
|---|---|---|
| Attack delivered | 140 tx/s, 16,801 rejections | none |
| EL CPU | 29% of one core | 3% |
| Admission p50 | 3.18 ms | n/a |

Honest traffic on the observed node: 241/241 included, p50 3.03 s, max 6.03 s, 7.9 tx/block.
Marginally better than the no-attack control.

**Admission latency is flat at ~3.2 ms from 12.5 to 140 tx/s**, an 11x range. Flat latency under
rising load means the work is absorbed by spare capacity rather than queued, so the node is well
below saturation.

Implied cost ~18 us per secp256k1 recovery (16,100 recoveries/s for 0.29 core-seconds/s).
Linear extrapolation puts single-core saturation near 480 tx/s against one node, on a 32-core
host.

Structural point: these transactions are rejected at admission and are therefore never gossiped.
An attacker cannot amplify through the network and must open a direct connection to every node
they want to load. That is a property of the design, not a tuning parameter, and it raises the
real cost of the attack.

---

## Ceiling axis at a fixed rate (the comparison the old data could not make)

Build cost scales with the ceiling, so the previous runs changed the generator's capability and
the client's workload at the same time. 75 tx/s is the highest rate every ceiling can generate
with 1.5x headroom, so it is the only honest common rate.

| Ceiling | Sigs/tx | Honest inclusion | Incl. p50 | Attacker admission p50 | EL CPU (both nodes) |
|---|---|---|---|---|---|
| 100,000 | 35 | 241/241 | 3.0 s | 1.58 ms | 6% / 6% |
| 236,285 | 84 | 241/241 | 3.4 s | 2.61 ms | 8% / 8% |
| 322,800 | 115 | 241/241 | 2.7 s | 3.46 ms | 10% / 10% |
| 500,000 | 178 | 241/241 | 3.4 s | 4.89 ms | 12% / 13% |

**Full honest inclusion at every ceiling, including 500,000.**

### The cost model

Least squares on the first three points:

```
admission_ms = 0.734 + 23.3 us x signatures
```

Fitted on 35 to 115 signatures, then used to predict the 178-signature point before it was
measured: **predicted 4.88 ms, measured 4.89 ms, 0.2% error.** Extrapolating 55% beyond the
fitted range without overshoot rules out a superlinear term, a cache cliff, or a queueing effect
anywhere in the range the campaign cares about.

Interpretation: `MAX_VERIFY_GAS` buys the attacker one secp256k1 recovery per 2,800 gas, at
23.3 us each, on top of a 0.73 ms fixed cost per transaction that the ceiling does not affect.

### What the parameter choice actually trades

| Ceiling | Admission cost | Rate to saturate one core |
|---|---|---|
| 100,000 | 1.55 ms | 646 tx/s |
| 236,285 | 2.69 ms | 372 tx/s |
| 322,800 | 3.41 ms | 293 tx/s |
| 500,000 | 4.88 ms | 205 tx/s |

100,000 -> 500,000 is a **5x** ceiling increase for a **3.2x** reduction in the attack rate
needed, because the fixed 0.73 ms dilutes the gain. Leverage is sublinear in the ceiling.

Caveat: these are single-core figures on a 32-core host, and measured CPU sits below the
single-core prediction, so recovery appears to parallelise. Saturating one core is not an
outage. Read the column as cost per unit of attacker effort, not as a denial-of-service
threshold.

### Concentrated load holds the same line

At ceiling 322,800, 140 tx/s aimed at one node gave admission p50 3.18 ms, against 3.46 ms for
75 tx/s spread across two. **Flat from 12.5 to 140 tx/s, an 11x range.** Flat latency under
rising load means spare capacity, not queueing.

---

## Repeatability and shape comparison (ceiling 322,800, 75 tx/s)

Three independent enclave launches of the same cell:

| Run | Attacker admission p50 | Honest inclusion | Incl. p50 |
|---|---|---|---|
| v3-ceil322800-r75 | 3450.0 us | 241/241 | 2.71 s |
| v4-...-rep2 | 3453.1 us | 241/241 | 2.98 s |
| v4-...-rep3 | 3460.0 us | 241/241 | 2.86 s |

**Admission cost: mean 3454.4 us, sd 5.1 us, CV 0.15%.** Inclusion p50 CV is 4.8%, which is slot
quantisation on a 6 s slot.

Note what this does and does not show. It establishes that the admission-cost measurement has
real resolving power, so a 10% gap between shapes is roughly 60 sd wide and is a genuine
difference. It says nothing about accuracy: the withdrawn data was not noisy either, it was
precisely and repeatably wrong. A harness can be perfectly consistent about the wrong quantity.

### Different work types, same ceiling

| Shape | Work performed | Rejection reason | Admission p50 | Honest inclusion |
|---|---|---|---|---|
| signature-stuffed | 115 secp256k1 recoveries | signer does not match | 3454 us | 241/241 |
| keccak-wide | 4 KB keccak loop in the EVM | validation prefix reverted | 3099 us | 241/241 |

Two unrelated execution paths, native crypto in the recovery layer versus EVM interpretation
ending in a revert, land within **10%** of each other under the same gas ceiling. That is
evidence the gas schedule prices the two kinds of unpaid validation work consistently, which is
the property MAX_VERIFY_GAS needs to be a meaningful bound rather than one that only holds for
the shapes we happened to test.

Both shapes read as benign in the withdrawn data for unrelated reasons: keccak-wide carries one
signature, so the old generator never saturated on it.

### Gas is a faithful proxy for client CPU across shapes

Adding the Groth16 shape (a real verifier contract with a deliberately invalid proof) gives three
unrelated execution paths. All three declare the same ceiling, but the Groth16 verifier only
consumes 248,437 gas of it, so the comparison has to normalise by gas actually used.

| Shape | Work | Rejected on | Admission p50 | Gas used | us / 1000 gas |
|---|---|---|---|---|---|
| signature-stuffed | 115 secp256k1 recoveries | signer does not match | 3454 us | 322,400 | 10.71 |
| keccak-wide | 4 KB keccak loop, out of gas | prefix reverted | 3099 us | 322,800 | 9.60 |
| groth16-soispoke | real verifier, invalid proof | prefix never set a payer | 2401 us | 248,437 | 9.66 |

**~10 us of client CPU per 1,000 gas of unpaid validation work, 12% spread.** Honest inclusion
was 241/241 in all three.

This is the property MAX_VERIFY_GAS depends on. The ceiling is denominated in gas, so it bounds
attacker leverage only if gas is a faithful proxy for client CPU across the shapes an attacker
could pick. It is, to within 12%. No cheap corner of the gas schedule gives materially more CPU
per unit of ceiling than these three.

Do not decompose this into fixed and marginal cost per shape. The 0.734 ms intercept was fitted
on signature-stuffed alone, and the shapes differ by an order of magnitude in transaction size
(115 signatures is ~11 KB of RLP; the Groth16 calldata is small), so per-transaction overhead is
not shared between them. Subtracting a common intercept widens the spread to 26% and is not
supported by this data.

---

## Limitations that remain, and matter for publication

- **Blocks are far from full.** 7.9 transactions per block, negligible gas. There is no
  contention for block space, so this measures admission cost only. On a chain with full blocks
  the same attack could matter through fee competition rather than through CPU. Not tested.
- **Honest baseline is 2 tx/s**, a small load. A denial claim about heavier honest traffic is not
  supported either way.
- **Two nodes co-located on one 32-core host**, plus the generator. Per-node isolation is weak,
  and the generator now has real throughput, so host contention is the next confound to rule out.
- **The generator's capacity still varies with the ceiling** (658 to 140 tx/s). Any ceiling
  comparison must fix the rate below the lowest ceiling's limit, or it reintroduces C1 in
  milder form.
- **This measures the JSON-RPC admission path, not devp2p.** Nethermind's own config docs note
  that a gossiped transaction never waits for a busy simulator while a locally submitted one
  does, so the two paths are not interchangeable. A flood arriving over p2p is untested. This
  interacts with the point above about rejected transactions not being gossiped: both need a
  p2p-level experiment to settle.

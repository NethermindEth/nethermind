---
name: gas-benchmark
description: Build a diag Docker image, run gas-benchmarks repricing workflow, and analyze results including dotTrace XML reports. Use when asked to "run benchmarks", "trigger gas benchmarks", "benchmark this branch", or "profile block processing".
disable-model-invocation: true
allowed-tools:
  - Bash(gh *)
  - Bash(git *)
  - Bash(cd *)
  - Bash(ls *)
  - Bash(mkdir *)
  - Bash(find *)
  - Bash(unzip *)
  - Bash(cat *)
  - Bash(wc *)
  - Bash(sleep *)
  - Bash(until *)
  - Bash(date *)
  - Read
  - Grep
  - Glob
argument-hint: "[--branch NAME] [--image NAME] [--filter PATTERN] [--release TAG] [--network NETWORK] [--no-diag] [--fork FORK] [--gas-benchmarks-ref REF]"
---

# Gas Benchmark Pipeline

End-to-end pipeline: build diag Docker image, trigger gas-benchmarks repricing workflow, wait for completion, and analyze results (logs, timings, dotTrace XML).

## Argument parsing

Parse `$ARGUMENTS` for these flags (all optional, use defaults when omitted):

| Flag | Default | Description |
|------|---------|-------------|
| `--branch` | current git branch | Nethermind branch to build the Docker image from |
| `--image` | (built from branch) | Skip Docker build; use this pre-built image directly |
| `--filter` | (none) | Test filter pattern passed to repricing workflow |
| `--release` | `amsterdam-repricings-v4.2.0` | GitHub release tag for test data |
| `--network` | `perf-devnet-3` | Network name (perf-devnet-3, jochemnet, mainnet) |
| `--no-diag` | (diag enabled) | Use regular Dockerfile instead of Dockerfile.diag |
| `--fork` | `amsterdam` | Fork name (amsterdam, osaka) |
| `--gas-benchmarks-ref` | `feat/stateful-generator-nethermind-diag` | Branch of gas-benchmarks repo to run workflow from |
| `--dottrace` | (disabled) | Enable dotTrace profiling (requires diag image) |

## Phase 1 — Docker image

Skip if `--image` is provided.

1. Determine the Nethermind branch (from `--branch` or `git branch --show-current`).
2. Determine Dockerfile: `Dockerfile.diag` unless `--no-diag` is set.
3. Compute tag: `<branch-name>-diag` (or `<branch-name>` if `--no-diag`).
4. Trigger the docker build:
   ```
   MSYS_NO_PATHCONV=1 gh workflow run publish-docker.yml \
     --ref <branch> \
     -f image-name=nethermind \
     -f tag=<tag> \
     -f dockerfile=<dockerfile> \
     -f build-config=release
   ```
5. Poll until complete: `gh run view <run-id> --json status,conclusion`
6. If build fails, fetch logs and report the error. Stop.
7. Final image: `nethermindeth/nethermind:<tag>`

## Phase 2 — Trigger repricing workflow

Determine the genesis file from the network:
- `perf-devnet-3` → `generator-amsterdam-perf-devnet-3.json`
- `jochemnet` → `generator-amsterdam-jochemnet.json`
- `mainnet` → (no genesis_file flag needed)

Determine test path from the network: `repricings_stateful/<network>`

Build and run the workflow trigger:
```
MSYS_NO_PATHCONV=1 gh workflow run repricing-nethermind.yml \
  --repo NethermindEth/gas-benchmarks \
  --ref <gas-benchmarks-ref> \
  -f test="repricings_stateful/<network>" \
  -f fork="<fork>" \
  -f release_tag="<release>" \
  -f genesis_file="<genesis-file>" \
  -f filter="<filter>" \
  -f 'runner=["stateful-generator"]' \
  -f 'images={"nethermind":"<image>"}'
```

Add these flags only when `--dottrace` is set AND image is a diag build:
```
  -f diagnostics_mode="dottrace" \
  -f diagnostics_xml="true"
```

Important: Do NOT pass `diagnostics_mode=dottrace` if the image was not built with `Dockerfile.diag` — the container will crash with `exec: dottrace: not found`.

Report the run URL to the user immediately after triggering.

## Phase 3 — Wait for completion

1. Get the run ID from `gh run list --repo NethermindEth/gas-benchmarks --workflow=repricing-nethermind.yml --limit 1`.
2. Poll: `gh run view <run-id> --repo NethermindEth/gas-benchmarks --json status,conclusion` every 30 seconds.
3. Report to the user when the run completes with success or failure.

## Phase 4 — Analyze results

### 4a. Mandatory checks

Fetch job logs: `gh run view --job=<job-id> --repo NethermindEth/gas-benchmarks --log`

Strip ANSI escape codes from all log output before analysis: `sed 's/\x1b\[[0-9;]*m//g'`

**Fail the analysis if any of these appear in Nethermind logs:**
- `Exception` (ignore Docker/pip/node-exporter exceptions)
- `Invalid Block` / `Invalid Blocks`
- `InvalidBlockLevelAccessListHash`
- `InvalidBlockLevelAccessListException`

**Flag if present:** `Unhandled`, `Fatal`, `ERROR` (in Nethermind context only)

**Confirm shutdown:** `Nethermind is shut down`

### 4b. Timing analysis

Extract all `Processed` lines. For each test block (blocks with `Gas gwei` in the log line):
- Report block number, processing time (ms), slot time (ms)
- Identify which test scenario each block belongs to (match against preceding `[TESTING]` log lines)

Sort by processing time descending. Report top 10 heaviest blocks.

Compute summary stats across test blocks: AVG, MEDIAN, P95, MAX.

### 4c. Block stats

Extract `Block` stats lines (the ones with `sload`, `sstore`, `create` counts).
Report sload/sstore/create counts for the heaviest test blocks.

### 4d. Opcode tracing comparison (when comparing two runs)

If the user asks to compare runs, download `opcodes_tracing-stateful-<network>.json` from the relevant release(s) and compare opcode counts for the specific test to confirm the workload is identical.

### 4e. dotTrace analysis (when available)

If dotTrace XML artifacts exist:
1. Download: `gh run download <run-id> --repo NethermindEth/gas-benchmarks -n "repricing-nethermind-dottrace-xml-<run-id>"`
2. Analyze using the repo's script: `bash scripts/dottrace-report.sh top <report.xml> 20`
3. For comparison: `bash scripts/dottrace-report.sh compare <baseline.xml> <new.xml> 20`
4. **Never load full XML into context** — files are 50-70MB.

## Phase 5 — Report

Present a summary table:

```
| Metric | Value |
|--------|-------|
| Branch | ... |
| Image | ... |
| Run URL | ... |
| Status | success/failure |
| Test block | #N |
| Processing time | X ms |
| sstore count | N |
| sload count | N |
| Exceptions | none / list |
```

If comparing against a baseline, include both timings and the speedup ratio.

## Common filter formats

The test filter must match the exact parameterized test name format from the release. Examples:
- `test_sstore_bloated[10GB-fork_Amsterdam-benchmark_test-cache_strategy_CacheStrategy.NO_CACHE-existing_slots_True-write_new_value_False-benchmark_300M`
- `bloated` (matches all bloated tests)
- (empty = run all tests in the release)

## Release tag conventions

- `amsterdam-repricings-v4.2.0` — perf-devnet-3 stateful + compute
- `amsterdam-repricings-v4.3.0` — updated test data
- `amsterdam-repricings-v5.0.0` — jochemnet stateful

Check available releases: `gh release list --repo NethermindEth/gas-benchmarks --limit 10`

## Gas-benchmarks branch conventions

- `feat/stateful-generator-nethermind-diag` — standard branch with diag support for perf-devnet-3
- `feat/stateful-generator-nethermind-diag-devnet-6` — devnet-6 variant with extended workflow inputs

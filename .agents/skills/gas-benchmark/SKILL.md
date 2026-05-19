---
name: gas-benchmark
description: Build a diag Docker image, run gas-benchmarks repricing workflow, and analyze results including dotTrace XML reports. Use when asked to "run benchmarks", "trigger gas benchmarks", "benchmark this branch", or "profile block processing".
allowed-tools:
  - Bash(gh run *)
  - Bash(gh workflow run *)
  - Bash(gh release *)
  - Bash(gh api repos/NethermindEth/*)
  - Bash(git branch *)
  - Bash(git log *)
  - Bash(git status *)
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
argument-hint: "[--branch NAME] [--image NAME] [--filter PATTERN] [--network NETWORK] [--fork FORK] [--dottrace]"
---

# Gas Benchmark Pipeline

End-to-end pipeline: build diag Docker image, trigger gas-benchmarks repricing workflow, wait for completion, and analyze results (logs, timings, dotTrace XML).

## Interactive mode (no arguments)

When called without arguments (`/gas-benchmark`), do NOT proceed with defaults. Instead, interactively gather the required information:

1. **Show available releases** and ask the user to pick one:
   ```
   gh api repos/NethermindEth/gas-benchmarks/releases?per_page=15 \
     --jq '.[] | "- `" + .tag_name + "` " + (if .draft then "(draft)" else "" end) + " — " + .name'
   ```
   Ask: "Which release should I use for test data?"

2. **Ask for the image**: "Which Nethermind Docker image? (e.g., `nethermindeth/nethermind:bal-devnet-6`) Or should I build one from a branch?"

3. **Ask for the network**: "Which network? (`perf-devnet-3`, `jochemnet`, `mainnet`)"

4. **Ask for filter**: "Any test filter? (e.g., `bloated`, or leave empty for all tests)"

5. **Ask about dotTrace**: "Do you want dotTrace profiling? (requires building a diag image, adds ~2min to build)"

Then proceed with the resolved values.

When called WITH arguments, parse them and proceed directly — only ask if something essential is missing or ambiguous.

## Argument parsing

Parse `$ARGUMENTS` for these flags:

| Flag | Default | Description |
|------|---------|-------------|
| `--branch` | current git branch | Nethermind branch to build the Docker image from |
| `--image` | (built from branch) | Skip Docker build; use this pre-built image directly |
| `--filter` | (none) | Test filter pattern passed to repricing workflow |
| `--network` | `perf-devnet-3` | Network name (perf-devnet-3, jochemnet, mainnet) |
| `--fork` | `amsterdam` | Fork name (amsterdam, osaka) |
| `--dottrace` | (ask user) | Enable dotTrace profiling — builds diag image, passes diagnostics flags |
| `--release` | (discovered) | Override release tag — skips interactive selection |
| `--gas-benchmarks-ref` | (discovered) | Override gas-benchmarks branch — skips discovery |

## Phase 0 — Discover gas-benchmarks branch and release

### Step 0a: Resolve the release

If `--release` was provided, use it. Otherwise (in non-interactive mode), discover the latest release for the fork:
```
gh api repos/NethermindEth/gas-benchmarks/releases?per_page=15 \
  --jq '[.[] | select(.tag_name | startswith("<fork>"))] | first | .tag_name'
```
Verify the release has data for the requested network:
```
gh release view <tag> --repo NethermindEth/gas-benchmarks --json assets --jq '.assets[].name'
```
Look for `generated-tests-stateful-<network>.tar.gz`.

### Step 0b: Find the gas-benchmarks branch

If `--gas-benchmarks-ref` was provided, use it. Otherwise, **extract from the release notes**:

1. The release body contains a `**Branch:**` field that records which gas-benchmarks branch generated the test data. Parse it:
   ```
   gh release view <tag> --repo NethermindEth/gas-benchmarks --json body --jq '.body' \
     | grep -oP '(?<=\*\*Branch:\*\* ).*' | tr -d '`' | xargs
   ```
   On Windows/Git Bash where `grep -P` may not work:
   ```
   gh release view <tag> --repo NethermindEth/gas-benchmarks --json body --jq '.body' \
     | grep "Branch:" | sed 's/.*Branch:\*\* *//; s/`//g' | xargs
   ```

2. If the release notes don't contain a branch field, fall back to listing branches:
   ```
   gh api repos/NethermindEth/gas-benchmarks/branches?per_page=100 \
     --jq '.[].name' | grep -E "devnets/bal|stateful-generator"
   ```
   Ask the user which branch to use.

3. Verify the workflow exists on the chosen branch:
   ```
   gh api repos/NethermindEth/gas-benchmarks/contents/.github/workflows/repricing-nethermind.yml?ref=<branch> --jq '.name' 2>/dev/null
   ```

### Step 0c: Discover workflow inputs

Read the workflow YAML on the chosen branch to learn which inputs it supports:
```
gh api repos/NethermindEth/gas-benchmarks/contents/.github/workflows/repricing-nethermind.yml?ref=<branch> \
  --jq '.content' | base64 -d
```
Note which of these inputs exist: `release_tag`, `genesis_file`, `runner`, `diagnostics_mode`, `diagnostics_xml`. Only pass flags the workflow declares.

### Step 0d: Determine genesis file

Map network to genesis filename:
- `perf-devnet-3` → `generator-amsterdam-perf-devnet-3.json`
- `jochemnet` → `generator-amsterdam-jochemnet.json`
- `mainnet` → (no genesis_file flag)

### Step 0e: Confirm with user

Before proceeding, show the resolved configuration:
```
Release:          <tag>
Gas-benchmarks:   <branch>
Network:          <network>
Image:            <image or "will build from <branch>">
Filter:           <filter or "none (all tests)">
dotTrace:         <yes/no>
```
Ask: "Proceed?"

## Phase 1 — Docker image

Skip if `--image` is provided.

1. Determine the Nethermind branch (from `--branch` or `git branch --show-current`).
2. Determine Dockerfile based on dotTrace:
   - dotTrace enabled → `Dockerfile.diag`, tag suffix `-diag`
   - dotTrace disabled → regular `Dockerfile`, no suffix
3. Compute tag: sanitize the branch name (replace `/` with `-`) then append suffix:
   ```
   TAG=$(echo "<branch-name>" | tr '/' '-')
   ```
   Final tag: `<TAG>-diag` (if diag) or `<TAG>` (if regular).
4. Capture timestamp, then trigger the docker build:
   ```
   BEFORE=$(date -u +%Y-%m-%dT%H:%M:%SZ)
   MSYS_NO_PATHCONV=1 gh workflow run publish-docker.yml \
     --ref <branch> \
     -f image-name=nethermind \
     -f tag=<tag> \
     -f dockerfile=<dockerfile> \
     -f build-config=release
   ```
5. Wait ~10s, then find the run ID using the timestamp to avoid race conditions:
   ```
   gh run list --workflow=publish-docker.yml --limit 5 --json databaseId,createdAt \
     --jq '[.[] | select(.createdAt > "<BEFORE>")] | first | .databaseId'
   ```
6. Poll until complete: `gh run view <run-id> --json status,conclusion`
7. If build fails, fetch logs and report the error. Stop.
8. Final image: `nethermindeth/nethermind:<tag>`

## Phase 2 — Trigger repricing workflow

Capture timestamp before triggering: `BEFORE=$(date -u +%Y-%m-%dT%H:%M:%SZ)`

Build the workflow trigger using only the inputs the workflow accepts (from Step 0c):
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

Only add diagnostics flags when `--dottrace` is set AND image is a diag build:
```
  -f diagnostics_mode="dottrace" \
  -f diagnostics_xml="true"
```

**Critical:** Do NOT pass `diagnostics_mode=dottrace` if the image was not built with `Dockerfile.diag` — the container will crash with `exec: dottrace: not found`.

Report the run URL to the user immediately after triggering.

## Phase 3 — Wait for completion

1. Find the run ID using the timestamp captured before triggering (same approach as Phase 1 step 5):
   ```
   gh run list --repo NethermindEth/gas-benchmarks --workflow=repricing-nethermind.yml \
     --limit 5 --json databaseId,createdAt \
     --jq '[.[] | select(.createdAt > "<BEFORE>")] | first | .databaseId'
   ```
2. Poll: `gh run view <run-id> --repo NethermindEth/gas-benchmarks --json status,conclusion` every 30 seconds.
3. **Timeout after 2 hours** (240 polls). If exceeded, report "timed out" and provide the run URL for manual inspection. Stop.
4. Report to the user when the run completes with success or failure.

## Phase 4 — Analyze results

**THIS PHASE IS MANDATORY. Always run it in full, even if the workflow reported success. Never skip or abbreviate it. A "success" workflow conclusion does NOT mean the blocks processed correctly — Nethermind exceptions can occur mid-run without failing the workflow.**

### 4a. Exception scan (NEVER SKIP)

Fetch job logs: `gh run view --job=<job-id> --repo NethermindEth/gas-benchmarks --log`

Strip ANSI escape codes: `sed 's/\x1b\[[0-9;]*m//g'`

Scan for ALL of these patterns. Report every match with the full log line:
```
grep -iE "Exception|Invalid Block|InvalidBlock|Rejected invalid" | grep -v "node-exporter\|pip install\|apt-get\|npm warn\|orphan process\|docker-compose\|nuget\.org"
```
Note: do NOT exclude `dotnet` — real Nethermind exceptions contain .NET runtime frames.

**Any match means the run has issues.** Classify:
- `HeaderGasUsedMismatch` → gas schedule mismatch between image and test data (wrong branch/fork)
- `InvalidBlockLevelAccessListHash` → BAL pre-state corruption (code bug)
- `InvalidBlockLevelAccessListException` → address/slot not in BAL (missing BAL entries)
- `Rejected invalid block ... reason: block is a part of an invalid chain` → cascade from earlier failure
- Any other `Exception` → report verbatim

**Always report the exception summary in the final report, even when there are zero exceptions.** Write "Exceptions: none" explicitly.

**Confirm shutdown:** grep for `Nethermind is shut down` — if absent, the node crashed or was killed.

### 4b. Timing analysis

Extract test block timings:
```
grep "Processed" <logs> | grep "Gas gwei"
```
For each match, report block number, processing time (ms), slot time (ms).
Match each block to its test scenario using the preceding `[TESTING]` log line.

Sort by processing time descending. Report top 10 heaviest blocks.

### 4c. Block stats

Extract block operation counts:
```
grep -E "Block.*sload|Block.*sstore" <logs> | grep -v "sstore     10"
```
Report sload/sstore/create counts for the heaviest test blocks.

### 4d. Opcode tracing comparison (only when comparing two runs)

When the user asks to compare runs, download the opcode tracing from each release:
```
gh release download <tag> --repo NethermindEth/gas-benchmarks \
  --pattern "opcodes_tracing-stateful-<network>.json" -D /tmp/tracing-<tag> --clobber
```
Parse the JSON and compare opcode counts for the specific test to confirm the workload is identical.

### 4e. dotTrace analysis (when `--dottrace` was enabled)

**Always check if the dotTrace XML artifact exists for the run:**
```
gh api repos/NethermindEth/gas-benchmarks/actions/runs/<run-id>/artifacts \
  --jq '.artifacts[].name' | grep "dottrace-xml"
```

If present:
1. **Download** the XML report:
   ```
   gh run download <run-id> --repo NethermindEth/gas-benchmarks \
     -n "repricing-nethermind-dottrace-xml-<run-id>" -D /tmp/dottrace-<run-id>
   ```

2. **Find the report file:**
   ```
   find /tmp/dottrace-<run-id> -name "*.xml" -not -name "*pattern*" -not -name "*conversion*"
   ```

3. **Top hotspots** — show the top 20 functions by OwnTime (self-time excluding callees):
   ```
   bash scripts/dottrace-report.sh top <report.xml> 20
   ```
   Output columns: `Function | OwnTime | TotalTime`
   - **OwnTime** = time spent in the function body itself (the hotspot indicator)
   - **TotalTime** = time including all callees
   - Sort by OwnTime descending — the top entries are where CPU time is actually spent

4. **Compare two runs** (when baseline available):
   ```
   bash scripts/dottrace-report.sh compare <baseline.xml> <new.xml> 20
   ```
   Output shows REGRESSIONS (B slower) and IMPROVEMENTS (B faster) with:
   - `[A] Own` / `[B] Own` = OwnTime in each run
   - `Delta` = absolute change (positive = regression)
   - `Change` = percentage change

5. **Interpretation guide:**
   - Functions with high OwnTime in `Nethermind.State`, `Nethermind.Trie`, `Nethermind.Db.Rocks` indicate storage/state bottlenecks
   - `RocksDbSharp` functions indicate disk I/O pressure
   - `Nethermind.Evm.VirtualMachine` functions indicate EVM execution overhead
   - System/GC functions (`System.GC`, `JIT_New`) indicate allocation pressure
   - Compare sstore/sload counts (from 4c) against OwnTime to distinguish I/O-bound vs compute-bound

6. **Never load full XML into context** — files are 50-70MB. Always use `scripts/dottrace-report.sh`.

## Phase 5 — Report

```
| Metric | Value |
|--------|-------|
| Branch | ... |
| Image | ... |
| Gas-benchmarks ref | ... |
| Release | ... |
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

To discover the exact filter format, check a previous run's logs:
```
gh run view <run-id> --repo NethermindEth/gas-benchmarks --log 2>/dev/null | grep "EFFECTIVE_FILTER"
```
Or check the test fixtures in the release archive. Examples:
- `test_sstore_bloated[10GB-fork_Amsterdam-benchmark_test-cache_strategy_CacheStrategy.NO_CACHE-existing_slots_True-write_new_value_False-benchmark_300M`
- `bloated` (matches all bloated tests)
- (empty = run all tests)

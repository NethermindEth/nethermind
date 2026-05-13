---
name: gas-benchmark
description: Build a diag Docker image, run gas-benchmarks repricing workflow, and analyze results including dotTrace XML reports. Use when asked to "run benchmarks", "trigger gas benchmarks", "benchmark this branch", "profile block processing", or "analyze benchmark run". Supports analyze-only mode for CI integration.
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
  - Bash(tar *)
  - Bash(wc *)
  - Bash(sleep *)
  - Bash(until *)
  - Bash(date *)
  - Bash(bash *)
  - Bash(sed *)
  - Bash(grep *)
  - Bash(awk *)
  - Bash(sort *)
  - Bash(head *)
  - Bash(tail *)
  - Read
  - Grep
  - Glob
argument-hint: "[--branch NAME] [--image NAME] [--filter PATTERN] [--network NETWORK] [--fork FORK] [--dottrace] [--analyze-run RUN_ID] [--compare RUN_ID]"
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

4. **Ask for filter** — help the user discover available tests first:
   a. After the release and network are known, list available test categories:
      ```
      gh release download <tag> --repo NethermindEth/gas-benchmarks \
        --pattern "generated-tests-stateful-<network>.tar.gz" -D /tmp/gb-tests --clobber
      tar tzf /tmp/gb-tests/generated-tests-stateful-<network>.tar.gz \
        | sed 's|.*/||' | sed 's/\.txt$//' | sed 's/\[.*//' | sort -u | grep -v "^$\|funding\|gas-bump"
      ```
   b. Show the user a categorized list of available tests. Example output:
      ```
      Available test categories for perf-devnet-3:
        - test_account_access     (CALL, STATICCALL, BALANCE, EXTCODE... variants)
        - test_sload_bloated      (large-state SLOAD scenarios)
        - test_sstore_bloated     (large-state SSTORE scenarios)
        - test_storage_sload_same_key_benchmark
      ```
   c. Ask: "Which tests do you want to run? You can:
      - Pick a category name (e.g., `sstore_bloated`)
      - Describe what you're interested in (e.g., 'storage write scenarios with existing slots')
      - Leave empty to run all tests"
   d. If the user gives a natural-language description, map it to the right filter pattern by inspecting the test parameter names in the archive (e.g., `existing_slots_True`, `write_new_value_False`, `CacheStrategy.NO_CACHE`).
   e. To show the user the full parameter space for a category:
      ```
      tar tzf /tmp/gb-tests/generated-tests-stateful-<network>.tar.gz \
        | grep "setup/.*<category>" | sed 's|.*/setup/||; s/\.txt$//' | head -20
      ```

5. **Ask about dotTrace**: "Do you want dotTrace profiling? (requires building a diag image, adds ~2min to build)"

Then proceed with the resolved values.

When called WITH arguments, parse them and proceed directly — only ask if something essential is missing or ambiguous. If `--filter` contains a natural-language description (not a test name pattern), resolve it using the test discovery steps above.

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
| `--analyze-run` | (none) | Skip Phases 0-3; go straight to Phase 4 analysis on an existing run. Value is a run ID or URL (e.g., `--analyze-run 25725558942` or `--analyze-run https://github.com/NethermindEth/gas-benchmarks/actions/runs/25725558942`) |
| `--compare` | (none) | Compare two runs. Value is a second run ID or URL. Requires `--analyze-run` for the first run. Downloads dotTrace XMLs from both and runs `compare`. |

## Analyze-only mode (`--analyze-run`)

When `--analyze-run <RUN_ID>` is provided, skip Phases 0–3 entirely and jump to Phase 4 analysis. This is the primary mode for CI integration — a CI pipeline triggers the workflow itself and then invokes `/gas-benchmark --analyze-run <RUN_ID>` to get the analysis.

1. Extract run ID from the value (strip URL prefix if given).
2. Fetch run metadata: `gh run view <run-id> --repo NethermindEth/gas-benchmarks --json status,conclusion,jobs`
3. If the run is still in progress, poll until complete (same as Phase 3).
4. Proceed to Phase 4 with the run ID.

When `--compare <RUN_ID_B>` is also provided:
1. Analyze both runs independently (Phase 4a–4c for each).
2. Download dotTrace XMLs from both runs.
3. Run `bash scripts/dottrace-report.sh compare <a.xml> <b.xml> 20` for hotspot comparison.
4. In the final report, show side-by-side timing tables and delta percentages.

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

### 4b. Timing analysis — block phase classification

Blocks in the log belong to three phases, identifiable by the `Extra Data:` field in the `Received New Block:` log lines:

| Phase | Extra Data pattern | Meaning |
|-------|-------------------|---------|
| **Gas bump** | `Nethermind v...` | Empty blocks that ramp the gas limit. **Ignore for timing.** |
| **Setup** | `setup:...` | Pre-state preparation (deploying contracts, filling storage). **Ignore for timing.** |
| **Testing** | `testing:...` | Actual benchmark execution. **Only these matter.** |

**Step 1 — Identify phase boundaries:**
```
# Find block number ranges for each phase
grep "Received New Block" <logs> | grep "Extra Data" | \
  awk '/Nethermind v/{type="gasbump"} /setup:/{type="setup"} /testing:/{type="testing"} {
    match($0, /Block: *([0-9]+)/, m); print type, m[1]
  }' | sort -k1,1 -k2,2n | awk '{
    if($1!=prev) { if(prev) printf "%s: %s-%s (%d blocks)\n", prev, first, last, count; first=$2; count=0 }
    last=$2; count++; prev=$1
  } END { printf "%s: %s-%s (%d blocks)\n", prev, first, last, count }'
```

**Step 2 — CRITICAL: Check for zero testing blocks:**
```
grep "Received New Block" <logs> | grep "Extra Data.*testing:" | wc -l
```
If this returns 0, **the release has no testing payloads for the selected filter**. Report this prominently:
> ⚠️ **No testing blocks found.** The release `<tag>` does not contain testing payloads for filter `<filter>` on network `<network>`. All blocks were gas-bump or setup blocks. The timing data below reflects only setup overhead, NOT actual test execution. The release may be incomplete or the filter may be too restrictive.

**Step 3 — Extract test block timings (testing phase only):**
Correlate `Received New Block` lines (to identify phase) with `Processed` lines (to get timing) by block number. Only report timings for blocks in the testing phase.

```
# Get testing block numbers
grep "Received New Block" <logs> | grep "testing:" | \
  sed 's/.*Block: *//' | awk '{gsub(/[^0-9]/, "", $1); print $1}' | sort -un > /tmp/testing-blocks.txt

# Extract Processed timings only for testing blocks
grep "Processed" <logs> | grep "ms" | while read line; do
  block=$(echo "$line" | sed 's/.*Processed *//' | awk '{gsub(/[^0-9.]/, "", $1); print $1}')
  if grep -q "^${block}$" /tmp/testing-blocks.txt; then
    echo "$line"
  fi
done
```

**Step 4 — Compute percentiles for testing blocks only:**
Report: COUNT, MIN, MEDIAN, AVG, P90, P95, P99, MAX.

**Step 5 — Sort by processing time descending. Report top 10 heaviest test blocks.**
Match each block to its test scenario using the preceding `[INFO] [SETUP]` or scenario name log lines.

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

Always include the block phase breakdown first:

```
### Block Phases
| Phase | Block Range | Count | Description |
|-------|------------|-------|-------------|
| Gas bump | 24358001–24363001 | 5001 | Empty gas-limit ramp blocks |
| Setup | 24363002–24363003 | 180 | Pre-state preparation |
| Testing | 24363004–24363183 | 180 | Actual benchmark execution |
```

If testing block count is 0, display prominently:
> ⚠️ **RELEASE DATA ISSUE:** No testing blocks found for filter `<filter>`. The release `<tag>` may not contain testing payloads for this filter/network combination. All timings below are setup overhead only — not meaningful for benchmarking.

Then the summary table (timings ONLY from testing blocks):
```
| Metric | Value |
|--------|-------|
| Branch | ... |
| Image | ... |
| Gas-benchmarks ref | ... |
| Release | ... |
| Run URL | ... |
| Status | success/failure |
| Exceptions | none / list |
| Testing blocks | N |
| AVG processing | X ms |
| MEDIAN | X ms |
| P95 | X ms |
| P99 | X ms |
| MAX | X ms |
```

Then the top 10 heaviest test blocks table with scenario names.

If comparing against a baseline, include both timings and the delta/speedup percentage.

## CI integration

The workflow `.github/workflows/gas-benchmark-analysis.yml` runs the full gas-benchmark pipeline in CI via Claude Code. It executes ALL phases (build → trigger → wait → analyze) and posts results as a PR comment.

**Authorization:** Only members of the `NethermindEth/core` GitHub team can trigger via PR comments (verified via API team membership check).

### Trigger 1: PR comment (full run)
Comment on a PR to run the complete pipeline on the PR branch:
```
@claude-bench                                  # full run, all tests, dotTrace enabled
@claude-bench --filter sstore_bloated          # full run with test filter
@claude-bench --no-dottrace                    # full run without dotTrace
@claude-bench --image nethermindeth/nethermind:my-tag  # skip build, use existing image
```

### Trigger 1b: PR comment (analyze-only)
To analyze an already-completed run instead of starting a new one:
```
@claude-bench --analyze-run 25725558942
@claude-bench --analyze-run 25725558942 --compare 25700000000
```

### Trigger 2: Manual dispatch
```
gh workflow run gas-benchmark-analysis.yml \
  -f branch=my-feature-branch \
  -f filter=sstore_bloated \
  -f dottrace=true \
  -f pr_number=12345
```

### Trigger 3: Repository dispatch (from gas-benchmarks repo)
```
gh api repos/NethermindEth/nethermind/dispatches \
  -f event_type=gas-benchmark-analysis \
  -f 'client_payload={"branch":"my-branch","filter":"bloated","pr_number":"12345"}'
```

All modes run this skill with the appropriate flags and post results as a PR comment (if a PR number is available) or to the workflow step summary.

## Filter reference

### How to discover available tests
List test categories from a release archive:
```
gh release download <tag> --repo NethermindEth/gas-benchmarks \
  --pattern "generated-tests-stateful-<network>.tar.gz" -D /tmp/gb-tests --clobber
tar tzf /tmp/gb-tests/generated-tests-stateful-<network>.tar.gz \
  | sed 's|.*/||' | sed 's/\.txt$//' | sed 's/\[.*//' | sort -u | grep -v "^$\|funding\|gas-bump"
```

### How to explore parameters for a test category
```
tar tzf /tmp/gb-tests/generated-tests-stateful-<network>.tar.gz \
  | grep "setup/.*<category>" | sed 's|.*/setup/||; s/\.txt$//' | head -20
```

### Filter patterns
The `filter` input is a substring match against the test fixture filenames. Examples:
- `sstore_bloated` — all sstore_bloated variants
- `sload_bloated` — all sload_bloated variants
- `account_access` — all account access tests
- `existing_slots_True` — only tests with pre-existing storage slots
- `NO_CACHE` — only tests with no caching strategy
- (empty) — run all tests

---
name: gas-benchmark
description: Build a diag Docker image, run gas-benchmarks repricing workflow, and analyze results including dotTrace XML reports. Use when asked to "run benchmarks", "trigger gas benchmarks", "benchmark this branch", "profile block processing", or "analyze benchmark run". Supports analyze-only mode for CI integration.
allowed-tools:
  - Bash(gh run *)
  - Bash(gh workflow run *)
  - Bash(gh release *)
  - Bash(gh api repos/NethermindEth/*)
  - Bash(MSYS_NO_PATHCONV=1 gh *)
  - Bash(git branch *)
  - Bash(git log *)
  - Bash(git status *)
  - Bash(mkdir *)
  - Bash(find *)
  - Bash(unzip *)
  - Bash(cat *)
  - Bash(tar *)
  - Bash(wc *)
  - Bash(sleep *)
  - Bash(date *)
  - Bash(bash *)
  - Bash(sed *)
  - Bash(grep *)
  - Bash(awk *)
  - Bash(sort *)
  - Bash(head *)
  - Bash(tail *)
  - Bash(tr *)
  - Bash(xargs *)
  - Bash(base64 *)
  - Bash(echo *)
  - Bash(printf *)
  - Read
  - Grep
  - Glob
argument-hint: "[--branch NAME] [--image NAME] [--filter PATTERN] [--network NETWORK] [--fork FORK] [--dottrace|--no-dottrace] [--gas-size SIZE] [--no-restart] [--release TAG] [--gas-benchmarks-ref REF] [--analyze-run RUN_ID] [--compare RUN_ID]"
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
   a. After the release and network are known, list available test categories using the commands in "Filter reference — How to discover available tests" below.
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
   e. To show the user the full parameter space for a category, use "Filter reference — How to explore parameters" below.

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
| `--dottrace` / `--no-dottrace` | interactive: ask; non-interactive: off | dotTrace profiling — builds diag image, passes diagnostics flags. Non-interactive runs enable it only when `--dottrace` is passed (the CI workflow defaults dottrace to true and passes `--dottrace` explicitly; it omits the flag when disabled). |
| `--gas-size` | `100M` | Gas size filter (appended as `benchmark_<size>` to filter). Default 100M. |
| `--no-restart` | (false) | Disable restart-before-testing for stateful tests (restart is on by default) |
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

The genesis filename embeds the fork name. For a fork other than `amsterdam`, list the release assets (Step 0a command) and pick the matching `generator-<fork>-<network>.json` instead of the mapping above.

### Step 0e: Confirm with user

Before proceeding, show the resolved configuration:
```
Release:          <tag>
Gas-benchmarks:   <branch>
Network:          <network>
Image:            <image or "will build from <branch>">
Filter:           <filter or "none (all tests)">
Gas size:         <100M (default) or user-specified>
Restart on test:  <yes (default for stateful) / no>
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

Build the workflow trigger using only the inputs the workflow accepts (from Step 0c).

**Gas size filtering:** The `filter` input in `run.sh` supports AND logic using the `and` keyword. Comma-separated patterns use OR (any match), but within each entry `" and "` requires ALL parts to match.

Always append ` and benchmark_<gas-size>` to the user's filter to restrict to a single gas size. Default gas size is `100M` (override with `--gas-size`). Examples:
- User filter `bloated` → effective filter sent to workflow: `bloated and benchmark_100M`
- User filter `sstore_bloated` → effective filter: `sstore_bloated and benchmark_100M`
- No user filter → effective filter: `benchmark_100M`
- User says "all gas sizes for bloated" → effective filter: `bloated` (no gas size appended)
- User passes `--gas-size 200M` with filter `bloated` → effective filter: `bloated and benchmark_200M`

**Restart before testing:** For stateful tests (`repricings_stateful/`), always pass `restart_before_testing=true` unless `--no-restart` was specified. This restarts the execution client container before each measured test for clean measurements.

```
MSYS_NO_PATHCONV=1 gh workflow run repricing-nethermind.yml \
  --repo NethermindEth/gas-benchmarks \
  --ref <gas-benchmarks-ref> \
  -f test="repricings_stateful/<network>" \
  -f fork="<fork>" \
  -f release_tag="<release>" \
  -f genesis_file="<genesis-file>" \
  -f filter="<effective-filter-with-gas-size>" \
  -f 'runner=["stateful-generator"]' \
  -f 'images={"nethermind":"<image>"}' \
  -f restart_before_testing="true"
```

Only add diagnostics flags when `--dottrace` is set AND image is a diag build:
```
  -f diagnostics_mode="dottrace" \
  -f diagnostics_xml="true"
```

Only add `restart_before_testing` when the workflow supports it (check Step 0c) and the test is stateful. Omit if `--no-restart` was specified.

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

All snippets in this phase are working-directory-independent (absolute paths, no `cd`). Keep them that way: the Bash tool's working directory persists across tool calls while variables do not — rely on neither.

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

### 4b. Timing analysis — use results artifacts (NOT raw logs)

**Always use the results artifacts for timing data.** Do NOT parse `Processed` lines from raw logs — with restart-before-testing, block numbers repeat across test cycles, making log-based correlation unreliable.

**Step 1 — Download results artifacts:**
```bash
mkdir -p /tmp/gb-results
gh run download <run-id> --repo NethermindEth/gas-benchmarks \
  -n "results-1-nethermind-<cleaned-test-path>" -D /tmp/gb-results
for z in /tmp/gb-results/*.zip; do unzip -o "$z" -d /tmp/gb-results; done
```

**Step 2 — Extract per-test timings from result files:**
Each test produces a `nethermind_results_1_<test-name>.txt` file containing the `engine_newPayloadV<N>` timing (the actual block processing time). The newPayload version is fork-dependent — detect it from the files rather than hardcoding:

```bash
R=/tmp/gb-results/results
NP=$(grep -ohm1 "engine_newPayloadV[0-9]*" "$R"/nethermind_results_1_*.txt | head -1)
[ -n "$NP" ] || { echo "No engine_newPayloadV<N> timing found in result files" >&2; exit 1; }
for f in "$R"/nethermind_results_1_*.txt; do
  ms=$(grep -A3 "$NP:" "$f" | grep "Average:" | awk '{print $2}')
  name="${f##*/}"; name="${name#nethermind_results_1_}"; name="${name%.txt}"
  echo "$ms $name"
done | sort -rn
```

**Step 3 — Compute aggregates** (re-derive `R` and `NP` — variables do not persist between tool calls):
```bash
R=/tmp/gb-results/results
NP=$(grep -ohm1 "engine_newPayloadV[0-9]*" "$R"/nethermind_results_1_*.txt | head -1)
[ -n "$NP" ] || { echo "No engine_newPayloadV<N> timing found in result files" >&2; exit 1; }
for f in "$R"/nethermind_results_1_*.txt; do
  ms=$(grep -A3 "$NP:" "$f" | grep "Average:" | awk '{print $2}')
  [ -n "$ms" ] && echo "$ms"
done | awk '{sum+=$1; vals[NR]=$1; n=NR} END {
  asort(vals)
  printf "COUNT:%d AVG:%.1f MEDIAN:%.1f P90:%.1f P95:%.1f MAX:%.1f\n",
    n, sum/n, vals[int(n/2+0.5)], vals[int(n*0.9+0.5)], vals[int(n*0.95+0.5)], vals[n]
}'
```

### 4b-compare. Comparing two runs (artifact-based)

When comparing two runs (e.g., PR vs baseline), download both results artifacts to separate directories, then compare per-test timings:

```bash
# Download both
mkdir -p /tmp/gb-pr /tmp/gb-base
gh run download <pr-run-id> --repo NethermindEth/gas-benchmarks \
  -n "results-1-nethermind-<cleaned-test-path>" -D /tmp/gb-pr
gh run download <base-run-id> --repo NethermindEth/gas-benchmarks \
  -n "results-1-nethermind-<cleaned-test-path>" -D /tmp/gb-base
for z in /tmp/gb-pr/*.zip; do unzip -o "$z" -d /tmp/gb-pr; done
for z in /tmp/gb-base/*.zip; do unzip -o "$z" -d /tmp/gb-base; done

# Compare per-test (sorted by delta); detect the newPayload version PER RUN — the two runs may target different forks
PR=/tmp/gb-pr/results; BASE=/tmp/gb-base/results
PR_NP=$(grep -ohm1 "engine_newPayloadV[0-9]*" "$PR"/nethermind_results_1_*.txt | head -1)
BASE_NP=$(grep -ohm1 "engine_newPayloadV[0-9]*" "$BASE"/nethermind_results_1_*.txt | head -1)
[ -n "$PR_NP" ] && [ -n "$BASE_NP" ] || { echo "newPayload timing not found in one of the runs" >&2; exit 1; }
for f in "$PR"/nethermind_results_1_*.txt; do
  pr_ms=$(grep -A3 "$PR_NP:" "$f" | grep "Average:" | awk '{print $2}')
  base_ms=$(grep -A3 "$BASE_NP:" "$BASE/${f##*/}" 2>/dev/null \
    | grep "Average:" | awk '{print $2}')
  if [ -n "$pr_ms" ] && [ -n "$base_ms" ]; then
    short="${f##*/}"; short="${short#nethermind_results_1_}"; short="${short%.txt}"
    delta=$(awk "BEGIN{printf \"%.1f\", (($pr_ms-$base_ms)/$base_ms)*100}")
    echo "$delta|$pr_ms|$base_ms|$short"
  fi
done | sort -t'|' -k1 -n | while IFS='|' read -r d p b name; do
  printf "%7s%% | PR: %9s ms | Base: %9s ms | %s\n" "$d" "$p" "$b" "$name"
done
```

Present results as a markdown table sorted by delta, then show aggregates (AVG, MEDIAN, P90, P95, MAX) for both runs with delta percentages.

### 4c. Block stats

Extract block operation counts:
```
# the -v filter drops known noise rows from non-test blocks; the whitespace in "sstore     10" is exact and intentional
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

3. **Top hotspots** — show the top 20 functions by OwnTime (self-time excluding callees). The script path is relative to the nethermind repo root (the Bash tool's default working directory — Phase 4 snippets never change it):
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

Always include the block phase breakdown first. Ranges and counts come from the run's logs — they differ per release; the values below are placeholders, not expected numbers:

```
### Block Phases
| Phase | Block Range | Count | Description |
|-------|------------|-------|-------------|
| Gas bump | <first>-<last> | N | Empty gas-limit ramp blocks |
| Setup | <first>-<last> | N | Pre-state preparation |
| Testing | <first>-<last> | N | Actual benchmark execution |
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
| Gas size | 100M |
| Restart on test | yes/no |
| Run URL | ... |
| Status | success/failure |
| Exceptions | none / list |
| Testing blocks | N |
| AVG processing | X ms |
| MEDIAN | X ms |
| P90 | X ms |
| P95 | X ms |
| MAX | X ms |
```

Then the top 10 heaviest test blocks table with scenario names.

If comparing against a baseline, include both timings and the delta/speedup percentage.

## CI integration

`.github/workflows/gas-benchmark-analysis.yml` runs this skill in CI via Claude Code — triggered by `@claude-bench` PR comments (restricted to `NethermindEth/core` team members), manual dispatch, or repository dispatch from the gas-benchmarks repo. CI passes the skill flags through verbatim; results are posted as a PR comment when a PR number is available, otherwise to the workflow step summary. The primary CI mode is `--analyze-run <RUN_ID>` (see "Analyze-only mode"); trigger syntax details live in the workflow file.

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

**Note:** The gas-size constraint (`benchmark_100M` by default) is always appended automatically. You do not need to include it in the filter manually.

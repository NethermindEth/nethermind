# Reproducible Benchmark Workflow

This document describes `.github/workflows/run-expb-reproducible-benchmarks.yml`.

## Purpose

Run reproducible EXPB benchmarks for Nethermind on the `reproducible-benchmarks`
self-hosted runner, enforce log quality checks, and compare PR performance against
a cached `master` baseline.

## Triggers

- `workflow_dispatch`: manual run with full input control.
- `pull_request` (`labeled` event only): runs only when label is exactly
  `reproducible-benchmark`, and only for PRs from the same repository.
- `push` to `master`: always runs to refresh the cached baseline metrics.

## Runner and collision handling

- Benchmark job runs on: `runs-on: [self-hosted, reproducible-benchmarks]`.
- Concurrency guard:
  - `group: expb-reproducible-benchmark-runner-queue`
  - `cancel-in-progress: false`
- Result: benchmark jobs queue instead of colliding or cancelling each other.

## Configuration selection

The workflow maps `state_layout` + `payload_set` to config file:

- `flat` + `superblocks` -> `github-action-compressed-mainnet-flat.yaml`
- `flat` + `realblocks` -> `github-action-mainnet-flat.yaml`
- `halfpath` + `superblocks` -> `github-action-compressed-mainnet.yaml`
- `halfpath` + `realblocks` -> `github-action-mainnet.yaml`

Defaults for PR and `master` push runs are:

- `state_layout=halfpath`
- `payload_set=realblocks`

## Docker image handling

For the selected Nethermind branch:

- skip docker publish trigger for auto-built branches:
  - `master`
  - `paprika`
  - `release/*`
- for other branches, trigger `publish-docker.yml` and wait for completion.

The image tag is derived from sanitized branch name and injected into config by
replacing `<<DOCKER_TAG>>`.

## EXPB installation

The workflow installs/updates EXPB via:

- default source:
  - `uv tool install --force --from git+https://github.com/NethermindEth/execution-payloads-benchmarks expb`
- custom source supported with `expb_repo` and `expb_branch`.

## Rendered config and placeholders

The workflow never mutates source YAML in `/mnt/sda/expb-data`. It generates a
temporary rendered config in `${{ runner.temp }}` and passes it to `expb`.

Render-time changes:

- replace `<<DOCKER_TAG>>`
- replace `<<DELAY>>`
- rename scenario key from `nethermind:` to:
  - `nethermind-<state_layout>-<payload_set>-<branch>-delay<seconds>s:`
- append additional flags to `extra_flags` from input
  `additional_extra_flags` (comma or newline separated).

The rendered file is always deleted in a final cleanup step.

## Benchmark command

The run uses:

```bash
expb execute-scenarios \
  --config-file <rendered-config> \
  --per-payload-metrics \
  --per-payload-metrics-logs \
  --print-logs
```

## Termination and cleanup grace period

If the runner receives `TERM`/`INT`, the workflow:

- sends `TERM` to `expb`
- waits up to `cleanup_grace_seconds`
- force kills only if still running after grace period.

This is required because EXPB performs self-cleanup and needs time to finish it.

## Output analysis and quality gates

The workflow parses EXPB output and enforces:

- hard fail if any log line contains `Exception` (first 40 lines printed)
- hard fail if benchmark command did not finish successfully
- hard fail if per-payload metrics table cannot be parsed.

Metrics extracted from the per-payload table:

- `AVG`, `MEAN`, `P90`, `P95`, `P99`, `MIN`, `MAX`

## Baseline cache and PR comparison

On successful `master` push runs (without exceptions):

- metrics are saved to GitHub cache as baseline.

On PR label-triggered runs:

- baseline metrics are restored from cache
- PR vs baseline deltas are computed
- a sticky PR comment is created/updated with comparison table.

## Notes for maintainers and agents

- Review Nethermind logs in benchmark output for failures, especially
  `Exception` and invalid block related errors.
- The per-payload summary table at the end is the source of truth for timing
  statistics and PR-vs-master comparison.
# sync-metrics data branch

Machine-appended timing records for the **Sync Master Validation** workflow — one JSON object per
line in `history.jsonl`, written by the `publish-metrics` job of
`.github/workflows/sync-master-validation.yml`. Records dated before 2026-07-30 were backfilled
from retained workflow runs (job durations from the API, plus log-derived stage timings where the
logs were still available).

Fields per record:

| Field | Meaning |
|---|---|
| `run_id`, `date`, `commit` | Workflow run id, its `created_at` (UTC), head SHA |
| `event` | `workflow_run`/`push` = master validation; `workflow_dispatch` = manual/experimental image |
| `network`, `mode` | e.g. `mainnet` / `Flat` |
| `image` | Docker image under test (absent when unknown) |
| `job_min` | Wall time of the `Sync <network> (<mode>)` job, minutes |
| `snap_min` | "Starting snap sync." → "Snap sync completed." |
| `heal_min` | "Snap sync completed." → "STATE SYNC FINISHED" |
| `verify_min` | "Collecting trie stats" → "Verification complete." (Flat) / "Stats after finishing state" (HalfPath) |
| `drain_min` | First "Waiting for storage verification workers" → "Verification complete." (Flat tail) |
| `accounts`, `slots` | Verification counters (Flat) |
| `source` | `ci` (collected in-run) or `backfill` |

Stage fields are present only when their log markers were found. The dashboard on the repo's
GitHub Pages site charts this file. Do not edit by hand.

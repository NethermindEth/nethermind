# BAL catch-up benchmark

Run **Run BAL Catch-up Benchmark** from GitHub Actions, select a branch, and choose
`synthetic-memory`, `synthetic-disk`, or `fixture`. The workflow uses the amd64
`reproducible-benchmarks` runner and publishes BenchmarkDotNet reports and logs.
GitHub enables manual dispatch after this workflow first lands on the default branch.
Both BAL-on and BAL-off arms run in the same job. Disk runs reopen a fresh working
copy before every iteration; they do not clear the OS page cache.
In-memory runs support up to 128 blocks, the default flat-state reorg window;
use `synthetic-disk` for longer segments.

```sh
gh workflow run run-bal-catchup-benchmark.yml --ref <branch> -f mode=synthetic-disk -f blocks=64
```

For `fixture`, first export a segment with `scripts/bal-catchup-fixture.py` and
provision its directory, matching stopped flat-node pre-state and chainspec on the
runner. Pass their absolute paths as `fixture`, `database` and `chainspec`.
The segment must start immediately above the pre-state's persisted block; exported
blocks at or below that block are skipped. No real-chain fixture is bundled.
`scratch` defaults to `/mnt/sda` and needs room for one copy of the pre-state.
The source database is only read; temporary copies are removed after the run.

## Local runs

```sh
DOTNET_TieredCompilation=0 dotnet run -c Release --project src/Nethermind/Nethermind.Evm.Benchmark -- --filter '*BalCatchUpBenchmark*'
```

Without environment variables, the benchmark builds valid synthetic Amsterdam
segments in memory: `DevnetLike` (about 6 Mgas over 66 transactions), `StorageHeavy`
(many state writes), and `ComputeHeavy` (computation with little state access).
Synthetic cases cover flat and trie state; fixtures use flat state.

| Variable | Purpose |
| --- | --- |
| `NETHERMIND_BALCATCHUP_DB` | RocksDB directory; synthetic runs create genesis here, fixtures read their pre-state here. |
| `NETHERMIND_BALCATCHUP_WORKDIR` | Working copies; defaults to the DB directory for synthetic runs, temp for fixtures. |
| `NETHERMIND_BALCATCHUP_FIXTURE` | Export directory containing `segment.jsonl`; requires DB and chainspec. |
| `NETHERMIND_BALCATCHUP_CHAINSPEC` | Fixture chainspec file. |
| `NETHERMIND_BALCATCHUP_BLOCKS` | Segment length; defaults to 64 synthetic blocks or the whole fixture. |
| `NETHERMIND_BALCATCHUP_COLD=1` | Reopen a fresh disk copy each iteration; required for flat segments beyond `FlatDb.MinReorgDepth`. |
| `NETHERMIND_BALCATCHUP_DROP_CACHES=1` | Linux/root only: write back and clear the OS page cache before each iteration. |

Both arms process the whole segment through `IBranchProcessor` with receipts.
BAL-off uses normal execution and validation, including parallel execution when
enabled. Setup rejects fallback in BAL-on or reconstruction in BAL-off.
One operation is the entire segment. Cleanup prints median blocks/s and Mgas/s,
splitting processing from state commit; disk runs also report final persistence.
Compare timings only from the same runner and cache mode.

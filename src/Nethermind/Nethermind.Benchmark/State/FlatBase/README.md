# Flat-base point-read benchmark (Phase 0 gate)

Standalone micro-benchmark comparing cold/warm uniform-random point reads across three storage
backends holding **byte-identical** synthetic state data:

| Backend | What it is |
| --- | --- |
| `RocksDb` | Production flat layout: `ColumnsDb<FlatDbColumns>` with the default `DbConfig` `FlatDb`/`FlatAccountDb`/`FlatStorageDb` option strings and dedicated HyperClockCaches (300 MiB Account / 700 MiB Storage — the 30/70 split of the 1 GiB flat budget applied by `FlatRocksDbConfigAdjuster`). |
| `SortedArena` | Prototype "MDBX-like" flat base: one mmap'd arena file per kind (`ArenaFile`, `MADV_RANDOM` on Linux) holding one `SortedTable` (see `Nethermind.State.Flat/PersistedSnapshots/Sorted/FORMAT.md`) per key-prefix shard (256 shards), plus a tiny in-memory shard directory. A point read is an index-block seek + one 4 KiB data-page seek. |
| `Lmdb` | LMDB via the `LightningDB` package (benchmark-only dependency), bulk-loaded in key order with `MDB_APPEND`; read with `MDB_NOTLS | MDB_NORDAHEAD`. |

Keys and values replicate the production flat encodings (`BaseFlatPersistence`): accounts are 20-byte
truncated address hashes mapping to ~70–90-byte slim-RLP account values; slots are 52-byte split keys
(`[4B addrHash | 32B slotHash | 16B addrHash]`) mapping to 32-byte values. All values derive
deterministically from the generation counter, and every benchmark setup re-validates 1000 sampled
keys byte-for-byte on the selected backend — a mismatch fails the run.

## Dataset

Built once and reused across runs (a `dataset.marker` file records the parameters; changing scale or
format rebuilds). Controlled by environment variables:

- `NETH_FLATBENCH_DIR` — dataset root (default: `<temp>/neth-flatbench`). Put it on the disk you want
  to measure.
- `NETH_FLATBENCH_SCALE`:
  - `smoke` (default): 100k accounts / 500k slots. CI/local friendly (~100 MB per backend); the smoke
    numbers are **indicative only** — everything fits in cache.
  - `full`: 300M accounts / 1.2B slots (≥ 100 GB per backend). Build takes hours and needs a large
    disk plus tens of GB of RAM headroom for the RocksDB bulk load/compaction. Run it on purpose, on
    the target Linux box only.

```bash
NETH_FLATBENCH_SCALE=full NETH_FLATBENCH_DIR=/mnt/nvme/flatbench \
  dotnet run -c release --project src/Nethermind/Nethermind.Benchmark.Runner -- \
  -f '*FlatBasePointRead*'
```

The first benchmark process builds the dataset (spill → per-shard sorted bulk-load of all three
backends → validation); subsequent processes reuse it.

## Running

Parameters: backend × {hit, guaranteed-miss} × reader threads {1, 8, 32}. Each invocation performs
8192 reads split across the workers; results are reported per read.

- **Warm**: just run the filter above. In-process caches (RocksDB block cache, OS page cache for the
  mmap/LMDB) are hot after warmup.
- **Cold** (the decision numbers): cold means *page-cache-cold*. On Linux, before **each** measured
  pass:

  ```bash
  sync; echo 3 | sudo tee /proc/sys/vm/drop_caches
  ```

  and run a short single-shot job so warmup does not re-warm the cache, one parameter combination at
  a time, e.g.:

  ```bash
  dotnet run -c release --project src/Nethermind/Nethermind.Benchmark.Runner -- \
    -f '*FlatBasePointRead*' --warmupCount 0 --iterationCount 1 --invocationCount 1 --unrollFactor 1
  ```

  Repeat (drop caches → run) ≥ 5 times per combination and aggregate manually.

## Metrics to record

- p50/p99 per-read latency and reads/s per (backend, hit/miss, threads) — from the per-read means of
  the repeated cold passes (BDN's in-run percentiles are meaningless for single-shot cold runs).
- IOPS per read: run `iostat -x 1` on the dataset device during the pass; `r/s ÷ reads/s` gives
  physical reads per lookup (the arena's core claim is ~1 data-page read per hit; RocksDB pays
  index/filter misses on top).
- Peak RSS per backend (cache budget accounting: RocksDB holds a 1 GiB block cache; the arena and
  LMDB rely on the page cache).

## Go/no-go thresholds (from planning)

On cold uniform-random reads at the `full` scale (≥ 100 GB per backend):

- **Go** if the arena is ≥ **1.8× RocksDB** on cold random hits **and** ≥ **0.85× LMDB**;
- miss cost must be ≤ **1.2× LMDB** (the arena has no bloom filters — misses still binary-search a
  shard; if this fails, Phase 1 adds a per-shard filter before any production work);
- otherwise **no-go**: stop the flat-base workstream and record the numbers.

## Implementation notes

- `Sorted/` contains verbatim copies of the internal `SortedTable` machinery from
  `Nethermind.State.Flat/PersistedSnapshots/Sorted/` (no `InternalsVisibleTo` for benchmarks; copying
  beats widening production visibility). Keep them in sync with the originals.
- `LightningDB` is referenced **only** by `Nethermind.Benchmark` — never add it to shipping projects.
- Zero production code was changed for this benchmark.

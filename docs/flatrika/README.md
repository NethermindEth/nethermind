# PaprikaFlat notes and benchmarks

This document summarizes the PaprikaFlat experiment: what changed, how the flat
and Paprika-backed layouts are wired, how to reproduce the synthetic benchmarks,
and what the current results say.

## What changed

Paprika was restored as a Nethermind submodule under `src/Nethermind/Paprika`
and wrapped by the `Nethermind.Paprika` project. Nethermind now has a new flat
state layout:

```text
FlatLayout.PaprikaFlat
```

PaprikaFlat is not a replacement for the Ethereum Merkle Patricia trie. It is a
flat-state backend variant:

- account and storage rows are stored in Paprika;
- trie nodes and flat metadata remain in the flat RocksDB columns;
- account keys use the same trie path as flat state: `keccak(address)`;
- storage keys use `keccak(address)` plus `keccak(slot)`;
- the trie remains the authenticated structure used for roots, proofs, and
  consistency checks.

The node-facing flag set is:

```bash
--FlatDb.Enabled=true --FlatDb.Layout=PaprikaFlat
```

PaprikaFlat currently requires a prepared PaprikaFlat flat DB. Fresh flat sync,
flat DB import, snap tree sync, and flat iteration-based verification paths are
guarded or unsupported for this layout.

## Consistency model

PaprikaFlat uses Paprika for account and storage writes and RocksDB for trie-node
writes. Commit coordination is handled conservatively:

- the flat DB layout marker records `PaprikaFlat`;
- a pending-current-state marker is written before advancing metadata;
- Paprika commits use `FlushDataAndRoot` by default;
- restart checks fail closed if RocksDB metadata and Paprika's latest state root
  do not agree;
- missing account/storage deletes are no-ops to avoid unnecessary Paprika page
  growth.

The benchmark tool can also use `--paprika-flush-data-only`, but those results
must be treated as deferred-root-flush mode, not fully durable commit timing.

## Benchmark tool

The synthetic schema benchmark is exposed through `Nethermind.Benchmark.Runner`:

```powershell
dotnet run --no-build --project src\Nethermind\Nethermind.Benchmark.Runner\Nethermind.Benchmark.Runner.csproj -c release -- --state-db-schema --help
```

Supported schemas:

```text
trie-control, flat, paprika
```

`trie-control` is a synthetic flat-in-trie control. It is useful as a local
control for the benchmark harness, but it is not a full regular Ethereum MPT
state benchmark.

Default shape:

```text
accounts:                 300,000
storage-bearing accounts: 50%
storage slots/account:    4
read ops:                 250,000 total, split across account/storage/trie reads
write ops:                100,000 accounts
write mode:               crash-consistent
Paprika commit mode:      FlushDataAndRoot
```

## Reproducing small flat vs PaprikaFlat runs

Use a fresh `--base-path` for each run unless you explicitly want `--reuse`.

```powershell
$base = "artifacts\state-db-schema-flat-vs-paprika"
dotnet run --no-build --project src\Nethermind\Nethermind.Benchmark.Runner\Nethermind.Benchmark.Runner.csproj -c release -- `
  --state-db-schema `
  --schemas flat,paprika `
  --seed 1729 `
  --base-path $base
```

For larger generated runs, Paprika usually needs explicit mmap capacity:

```powershell
$base = "artifacts\state-db-schema-flat-vs-paprika-3m"
dotnet run --no-build --project src\Nethermind\Nethermind.Benchmark.Runner\Nethermind.Benchmark.Runner.csproj -c release -- `
  --state-db-schema `
  --schemas flat,paprika `
  --accounts 3000000 `
  --seed 1729 `
  --paprika-capacity-gb 16 `
  --base-path $base
```

## Reproducing random 3k-batch overwrite runs

`--random-writes` changes the write benchmark from append-style writes to
updates of prepared accounts. It uses a deterministic full-cycle permutation of
the prepared account range and salts written values so overwrites are real
mutations.

Random-written stores are marked with `random-writes.dirty` and cannot be reused
as pristine deterministic stores. Paprika random-write runs require explicit
capacity because history retention can exceed the default mmap size.

Write-focused 300k run:

```powershell
$base = "artifacts\state-db-schema-random-writes-3k-300k"
dotnet run --no-build --project src\Nethermind\Nethermind.Benchmark.Runner\Nethermind.Benchmark.Runner.csproj -c release -- `
  --state-db-schema `
  --schemas flat,paprika `
  --seed 1729 `
  --random-writes `
  --batch-accounts 3000 `
  --read-ops 4 `
  --paprika-capacity-gb 8 `
  --base-path $base
```

Write-focused 3M run:

```powershell
$base = "artifacts\state-db-schema-random-writes-3k-3m"
dotnet run --no-build --project src\Nethermind\Nethermind.Benchmark.Runner\Nethermind.Benchmark.Runner.csproj -c release -- `
  --state-db-schema `
  --schemas flat,paprika `
  --accounts 3000000 `
  --seed 1729 `
  --random-writes `
  --batch-accounts 3000 `
  --read-ops 4 `
  --paprika-capacity-gb 32 `
  --base-path $base
```

## Crafting a large deterministic dataset

The large local dataset used for the latest comparison was generated with:

```text
seed:                     1729
accounts:                 22,500,000
storage-bearing accounts: 11,250,000
storage slots:            45,000,000
generated rows:           135,000,000
storage percent:          50
slots/account:            4
trie node bytes:          1024
```

Equivalent prepare command shape:

```powershell
$base = "H:\flatrika-state-db-60g-seed-1729\pristine"
dotnet run --no-build --project src\Nethermind\Nethermind.Benchmark.Runner\Nethermind.Benchmark.Runner.csproj -c release -- `
  --state-db-schema `
  --schemas flat,paprika `
  --accounts 22500000 `
  --storage-percent 50 `
  --slots 4 `
  --trie-node-bytes 1024 `
  --batch-accounts 250000 `
  --seed 1729 `
  --paprika-capacity-gb 180 `
  --prepare-only `
  --base-path $base
```

For faster generation on a machine with a faster `D:` drive, generate under
`D:\...` and move the completed schema directory to the long-term disk after
verification. Do not use `--unsafe-disable-wal` for a durable PaprikaFlat DB; an
unsafe generated PaprikaFlat DB previously reopened with account/storage
checksum failures.

## Integrity checks

Bounded semantic CRC over an existing prepared store:

```powershell
dotnet run --no-build --project src\Nethermind\Nethermind.Benchmark.Runner\Nethermind.Benchmark.Runner.csproj -c release -- `
  --state-db-schema `
  --schemas flat,paprika `
  --skip-prepare `
  --prepare-only `
  --verify-all `
  --verify-accounts 1000 `
  --verify-parallelism 4 `
  --base-path H:\flatrika-state-db-60g-seed-1729\pristine
```

Raw file CRC over existing schema files:

```powershell
dotnet run --no-build --project src\Nethermind\Nethermind.Benchmark.Runner\Nethermind.Benchmark.Runner.csproj -c release -- `
  --state-db-schema `
  --schemas flat,paprika `
  --file-crc-only `
  --base-path H:\flatrika-state-db-60g-seed-1729\pristine
```

Full logical semantic verification over the 135M-row dataset was not practical
on the slower `H:` disk: the first 25k-account random point-read chunk did not
finish in 10 minutes. The practical integrity method for that dataset is raw
file CRC plus bounded semantic CRC.

## Results

### Default synthetic benchmark

Fully durable `FlushDataAndRoot`, default 300k prepared-account run:

| schema | reads | writes |
| --- | ---: | ---: |
| trie-control | 326,736 ops/s | 591,891 rows/s |
| flat | 361,826 ops/s | 608,119 rows/s |
| PaprikaFlat | 422,088 ops/s | 802,465 rows/s |

Fresh three-run average at 300k accounts, `seed=1729`:

| schema | reads | writes |
| --- | ---: | ---: |
| flat | 498,782 ops/s | 878,310 rows/s |
| PaprikaFlat | 585,946 ops/s | 948,600 rows/s |

Larger generated 3M-account run:

| schema | reads | writes | prepared size |
| --- | ---: | ---: | ---: |
| flat | 159,375 ops/s | 825,007 rows/s | 1.17 GiB logical / 2.42 GiB footprint |
| PaprikaFlat | 223,932 ops/s | 448,876 rows/s | 8.86 GiB logical / 9.64 GiB footprint |

### Random 3k-batch overwrite benchmark

Write-focused runs used `--read-ops 4` to avoid warming the random-write set.

| prepared accounts | schema | writes | mutate | commit |
| ---: | --- | ---: | ---: | ---: |
| 300,000 | flat | 753,630 rows/s | 0.211s | 0.583s |
| 300,000 | PaprikaFlat | 141,925 rows/s | 0.609s | 3.615s |
| 3,000,000 | flat | 683,001 rows/s | 0.226s | 0.650s |
| 3,000,000 | PaprikaFlat | 31,403 rows/s | 0.979s | 18.124s |

### Large 22.5M-account dataset

Final H: preserved stores:

| schema | logical size | live footprint | raw files |
| --- | ---: | ---: | ---: |
| flat | 70.17 GiB | 70.19 GiB | 70.19 GiB |
| PaprikaFlat | 128.65 GiB | 128.88 GiB | 248.21 GiB including 180 GiB mmap capacity file |

Integrity:

| check | result |
| --- | --- |
| bounded semantic CRC, 1,000 accounts | both `0x041a7484` |
| flat raw file CRC | `0x96924927` over 70.19 GiB |
| PaprikaFlat raw file CRC | `0x396de997` over 248.21 GiB |

Cold-process D: working-copy benchmark, 250k reads and 100k write accounts:

| schema | reads | writes |
| --- | ---: | ---: |
| flat | 6,636 ops/s | 311,408 rows/s |
| PaprikaFlat, default history | 4,309 ops/s | 78,385 rows/s |
| PaprikaFlat, history depth 3 | 5,495 ops/s | 37,369 rows/s |

PaprikaFlat is valid, but it does not overtake flat on the large dataset. It is
competitive on small/default synthetic reads, but random overwrite commits and
large prepared-state size are the main weaknesses.

## Workflow support

The reproducible benchmark workflow accepts the Paprika layout alongside flat
and halfpath layouts:

```text
halfpath, flat, paprika
```

For `paprika`, workflow rendering adds:

```text
--FlatDb.Enabled=true
--FlatDb.Layout=PaprikaFlat
```

Quality gates check for the PaprikaFlat backend marker in logs:

```text
State backend: flat (layout PaprikaFlat
```

The single-node workflow checkbox also maps to:

```text
FlatDb.Enabled=true FlatDb.Layout=PaprikaFlat
```

Those paths still require a prepared PaprikaFlat DB.


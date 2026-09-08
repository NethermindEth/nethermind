# Nethermind.Precompiles.Benchmark

BenchmarkDotNet benchmarks for the EVM precompiled contracts.

Most precompiles delegate to native libraries; RIPEMD-160 (`0x03`) is the only one whose
compute path is pure managed C# (BouncyCastle), so it is the only precompile where managed
allocation is directly addressable.

## Running

From `src/Nethermind`:

```bash
# All precompile benchmarks
dotnet run -c Release --project Nethermind.Benchmark.Runner -- --filter "*Benchmark*"

# A single benchmark (e.g. the RIPEMD-160 allocation benchmark)
dotnet run -c Release --project Nethermind.Benchmark.Runner -- --filter "*Ripemd160AllocationBenchmark*"

# Add --quick for a fast ShortRun (less accurate timings; allocation figures need a full run)
```

`Nethermind.Benchmark.Runner` enables `MemoryDiagnoser` for every benchmark, so the summary
includes the `Allocated` column.

## RIPEMD-160 digest reuse

`Ripemd.Compute` previously allocated a fresh BouncyCastle `RipeMD160Digest` (and its internal
`xBuf`/`X` working buffers) on every call. Because `DoFinal` resets the digest for reuse, a single
instance is now held per thread in a `[ThreadStatic]` field and reused, leaving only the unavoidable
32-byte output array to be allocated per call.

`Ripemd160AllocationBenchmark` confirms the effect by measuring a fresh-per-call digest
(`FreshDigestPerCall`, the pre-change behaviour) against the thread-static reuse
(`ReusedThreadStaticDigest`, `Ripemd.Compute`).

### Results

128-byte input, .NET 10, `DefaultJob` (MediumRun), 12th Gen Intel Core i7-12700H:

| Method                   | Mean     | Allocated | Alloc Ratio |
|------------------------- |---------:|----------:|------------:|
| `FreshDigestPerCall`     | 1.399 µs |     248 B |        1.00 |
| `ReusedThreadStaticDigest` | 1.409 µs |      56 B |        0.23 |

Allocation drops from **248 B to 56 B per call (-77%)**. The remaining 56 B is the 32-byte output
array plus its object header. Mean execution time is unchanged (the difference is within run-to-run
noise) — this is an allocation/GC-pressure optimization, not a compute one.

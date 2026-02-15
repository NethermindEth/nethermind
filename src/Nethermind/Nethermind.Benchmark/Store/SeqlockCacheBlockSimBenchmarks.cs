// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Int256;

namespace Nethermind.Benchmarks.Store;

/// <summary>
/// Benchmarks SeqlockCache under realistic cross-block access patterns:
/// - Epoch clear + refill (simulates state cache per-block rebuild)
/// - Incremental update (simulates storage cache delta replay)
/// - Concurrent read + write (simulates prewarmer reading while main processor applies deltas)
/// - Hit rate under varying working set sizes
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 2, iterationCount: 6)]
public class SeqlockCacheBlockSimBenchmarks
{
    private SeqlockCache<StorageCell, byte[]> _cache = null!;
    private StorageCell[] _cells = null!;
    private byte[][] _values = null!;

    [Params(1000, 10000, 50000)]
    public int EntryCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _cache = new SeqlockCache<StorageCell, byte[]>();
        Random random = new(42);
        byte[] addressBuffer = new byte[Address.Size];

        _cells = new StorageCell[EntryCount];
        _values = new byte[EntryCount][];

        for (int i = 0; i < EntryCount; i++)
        {
            random.NextBytes(addressBuffer);
            Address addr = new Address((byte[])addressBuffer.Clone());
            _cells[i] = new StorageCell(addr, (UInt256)i);
            _values[i] = ((UInt256)(i + 1)).ToBigEndian();
        }
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _cache.Clear();
    }

    [Benchmark(Baseline = true)]
    public int EpochClearThenRefill()
    {
        // Simulate state cache pattern: clear then rebuild from deltas
        _cache.Clear();

        int hits = 0;
        for (int i = 0; i < EntryCount; i++)
        {
            _cache.Set(in _cells[i], _values[i]);
        }

        // Read-back to measure cache effectiveness after refill
        for (int i = 0; i < EntryCount; i++)
        {
            if (_cache.TryGetValue(in _cells[i], out _))
            {
                hits++;
            }
        }

        return hits;
    }

    [Benchmark]
    public int IncrementalUpdate()
    {
        // Pre-populate cache to simulate storage cache with existing data
        for (int i = 0; i < EntryCount; i++)
        {
            _cache.Set(in _cells[i], _values[i]);
        }

        // Simulate delta replay: update half the entries with new values
        int deltaCount = EntryCount / 2;
        for (int i = 0; i < deltaCount; i++)
        {
            byte[] newValue = ((UInt256)(i + 99999)).ToBigEndian();
            _cache.Set(in _cells[i], newValue);
        }

        // Read-back everything to check hit rate
        int hits = 0;
        for (int i = 0; i < EntryCount; i++)
        {
            if (_cache.TryGetValue(in _cells[i], out _))
            {
                hits++;
            }
        }

        return hits;
    }

    [Benchmark]
    public int ConcurrentReadWrite()
    {
        // Pre-populate cache
        for (int i = 0; i < EntryCount; i++)
        {
            _cache.Set(in _cells[i], _values[i]);
        }

        int writerHits = 0;
        int readerHits = 0;

        // Simulate concurrent access: writer updates entries while readers query
        Task writer = Task.Run(() =>
        {
            int localHits = 0;
            for (int i = 0; i < EntryCount; i++)
            {
                byte[] newValue = ((UInt256)(i + 50000)).ToBigEndian();
                _cache.Set(in _cells[i], newValue);
                if (_cache.TryGetValue(in _cells[i], out _))
                {
                    localHits++;
                }
            }

            Interlocked.Add(ref writerHits, localHits);
        });

        Task reader = Task.Run(() =>
        {
            int localHits = 0;
            for (int i = EntryCount - 1; i >= 0; i--)
            {
                if (_cache.TryGetValue(in _cells[i], out _))
                {
                    localHits++;
                }
            }

            Interlocked.Add(ref readerHits, localHits);
        });

        Task.WaitAll(writer, reader);

        return writerHits + readerHits;
    }

    [Benchmark]
    public int WorkingSetOverflow()
    {
        // SeqlockCache has a fixed number of sets (16384 * 2 ways).
        // When EntryCount > capacity, measure eviction/hit-rate degradation.
        for (int i = 0; i < EntryCount; i++)
        {
            _cache.Set(in _cells[i], _values[i]);
        }

        int hits = 0;
        for (int i = 0; i < EntryCount; i++)
        {
            if (_cache.TryGetValue(in _cells[i], out _))
            {
                hits++;
            }
        }

        return hits;
    }
}

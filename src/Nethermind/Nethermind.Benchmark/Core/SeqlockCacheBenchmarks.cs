// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Int256;

namespace Nethermind.Benchmarks.Core;

[MemoryDiagnoser]
[DisassemblyDiagnoser(maxDepth: 3)]
public class SeqlockCacheBenchmarks
{
    private SeqlockCache<StorageCell, byte[]> _seqlockCache = null!;
    private ConcurrentDictionary<StorageCell, byte[]> _concurrentDict = null!;

    private StorageCell[] _keys = null!;
    private byte[][] _values = null!;
    private StorageCell _missKey;

    [Params(1000)]
    public int KeyCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _seqlockCache = new SeqlockCache<StorageCell, byte[]>();
        _concurrentDict = new ConcurrentDictionary<StorageCell, byte[]>();

        _keys = new StorageCell[KeyCount];
        _values = new byte[KeyCount][];

        var random = new Random(42);
        for (int i = 0; i < KeyCount; i++)
        {
            var addressBytes = new byte[20];
            random.NextBytes(addressBytes);
            var address = new Address(addressBytes);
            var index = new UInt256((ulong)i);

            _keys[i] = new StorageCell(address, index);
            _values[i] = new byte[32];
            random.NextBytes(_values[i]);

            // Pre-populate both caches
            _seqlockCache.Set(in _keys[i], _values[i]);
            _concurrentDict[_keys[i]] = _values[i];
        }

        // Create a key that won't be in the cache
        var missAddressBytes = new byte[20];
        random.NextBytes(missAddressBytes);
        _missKey = new StorageCell(new Address(missAddressBytes), UInt256.MaxValue);
    }

    // ==================== TryGetValue (Hit) ====================

    [Benchmark(Baseline = true)]
    public bool SeqlockCache_TryGetValue_Hit()
    {
        return _seqlockCache.TryGetValue(in _keys[500], out _);
    }

    [Benchmark]
    public bool ConcurrentDict_TryGetValue_Hit()
    {
        return _concurrentDict.TryGetValue(_keys[500], out _);
    }

    // ==================== TryGetValue (Miss) ====================

    [Benchmark]
    public bool SeqlockCache_TryGetValue_Miss()
    {
        return _seqlockCache.TryGetValue(in _missKey, out _);
    }

    [Benchmark]
    public bool ConcurrentDict_TryGetValue_Miss()
    {
        return _concurrentDict.TryGetValue(_missKey, out _);
    }

    // ==================== Set (Existing Key) ====================

    [Benchmark]
    public void SeqlockCache_Set_Existing()
    {
        _seqlockCache.Set(in _keys[500], _values[500]);
    }

    [Benchmark]
    public void ConcurrentDict_Set_Existing()
    {
        _concurrentDict[_keys[500]] = _values[500];
    }

    // ==================== GetOrAdd (Hit) ====================

    [Benchmark]
    public byte[]? SeqlockCache_GetOrAdd_Hit()
    {
        return _seqlockCache.GetOrAdd(in _keys[500], static (in StorageCell _) => new byte[32]);
    }

    [Benchmark]
    public byte[] ConcurrentDict_GetOrAdd_Hit()
    {
        return _concurrentDict.GetOrAdd(_keys[500], static _ => new byte[32]);
    }

    // ==================== GetOrAdd (Miss - measures factory overhead) ====================

    private int _missCounter;

    [Benchmark]
    public byte[]? SeqlockCache_GetOrAdd_Miss()
    {
        // Use incrementing key to always miss
        var key = new StorageCell(_keys[0].Address, new UInt256((ulong)(KeyCount + _missCounter++)));
        return _seqlockCache.GetOrAdd(in key, static (in StorageCell _) => new byte[32]);
    }

    [Benchmark]
    public byte[] ConcurrentDict_GetOrAdd_Miss()
    {
        var key = new StorageCell(_keys[0].Address, new UInt256((ulong)(KeyCount + _missCounter++)));
        return _concurrentDict.GetOrAdd(key, static _ => new byte[32]);
    }
}

/// <summary>
/// Benchmark comparing read-heavy workloads (90% reads, 10% writes)
/// </summary>
[MemoryDiagnoser]
public class SeqlockCacheMixedWorkloadBenchmarks
{
    private SeqlockCache<StorageCell, byte[]> _seqlockCache = null!;
    private ConcurrentDictionary<StorageCell, byte[]> _concurrentDict = null!;

    private StorageCell[] _keys = null!;
    private byte[][] _values = null!;

    private const int KeyCount = 10000;
    private const int OperationsPerInvoke = 1000;

    [GlobalSetup]
    public void Setup()
    {
        _seqlockCache = new SeqlockCache<StorageCell, byte[]>();
        _concurrentDict = new ConcurrentDictionary<StorageCell, byte[]>();

        _keys = new StorageCell[KeyCount];
        _values = new byte[KeyCount][];

        var random = new Random(42);
        for (int i = 0; i < KeyCount; i++)
        {
            var addressBytes = new byte[20];
            random.NextBytes(addressBytes);
            var address = new Address(addressBytes);
            var index = new UInt256((ulong)i);

            _keys[i] = new StorageCell(address, index);
            _values[i] = new byte[32];
            random.NextBytes(_values[i]);

            // Pre-populate both caches
            _seqlockCache.Set(in _keys[i], _values[i]);
            _concurrentDict[_keys[i]] = _values[i];
        }
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = OperationsPerInvoke)]
    public int SeqlockCache_MixedWorkload_90Read_10Write()
    {
        int hits = 0;
        for (int i = 0; i < OperationsPerInvoke; i++)
        {
            int keyIndex = i % KeyCount;
            if (i % 10 == 0)
            {
                // 10% writes
                _seqlockCache.Set(in _keys[keyIndex], _values[keyIndex]);
            }
            else
            {
                // 90% reads
                if (_seqlockCache.TryGetValue(in _keys[keyIndex], out _))
                    hits++;
            }
        }
        return hits;
    }

    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    public int ConcurrentDict_MixedWorkload_90Read_10Write()
    {
        int hits = 0;
        for (int i = 0; i < OperationsPerInvoke; i++)
        {
            int keyIndex = i % KeyCount;
            if (i % 10 == 0)
            {
                // 10% writes
                _concurrentDict[_keys[keyIndex]] = _values[keyIndex];
            }
            else
            {
                // 90% reads
                if (_concurrentDict.TryGetValue(_keys[keyIndex], out _))
                    hits++;
            }
        }
        return hits;
    }

    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    public int SeqlockCache_ReadOnly()
    {
        int hits = 0;
        for (int i = 0; i < OperationsPerInvoke; i++)
        {
            int keyIndex = i % KeyCount;
            if (_seqlockCache.TryGetValue(in _keys[keyIndex], out _))
                hits++;
        }
        return hits;
    }

    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    public int ConcurrentDict_ReadOnly()
    {
        int hits = 0;
        for (int i = 0; i < OperationsPerInvoke; i++)
        {
            int keyIndex = i % KeyCount;
            if (_concurrentDict.TryGetValue(_keys[keyIndex], out _))
                hits++;
        }
        return hits;
    }
}

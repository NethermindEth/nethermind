// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;

namespace Nethermind.Benchmarks.State;

/// <summary>Isolates the two dictionary lookups a warm storage read performs.</summary>
/// <remarks>The write journal is keyed by the whole <see cref="StorageCell"/>, whose hash has to chase
/// the <see cref="Address"/> reference and its byte array; the per-contract read cache is keyed by the
/// index alone. A read of an unwritten slot pays the first lookup as a pure miss before reaching the second.</remarks>
[MemoryDiagnoser]
public class StorageLookupBenchmark
{
    private const int OperationsPerInvoke = 1000;

    private Dictionary<StorageCell, byte[]> _byCell = null!;
    private Dictionary<UInt256, byte[]> _byIndex = null!;
    private StorageCell _present;
    private StorageCell _absent;
    private UInt256 _index;

    [Params(1, 64)]
    public int JournalEntries { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        byte[] value = new byte[32];
        _byCell = [];
        _byIndex = [];

        for (int i = 0; i < JournalEntries; i++)
        {
            _byCell[new StorageCell(TestItem.AddressA, (UInt256)i)] = value;
        }

        for (int i = 0; i < 64; i++)
        {
            _byIndex[(UInt256)i] = value;
        }

        _present = new StorageCell(TestItem.AddressA, UInt256.Zero);
        _absent = new StorageCell(TestItem.AddressA, (UInt256)10_000);
        _index = (UInt256)5;
    }

    [Benchmark(OperationsPerInvoke = OperationsPerInvoke, Baseline = true)]
    public int JournalMiss()
    {
        int found = 0;
        for (int i = 0; i < OperationsPerInvoke; i++)
        {
            if (_byCell.TryGetValue(_absent, out _)) found++;
        }
        return found;
    }

    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    public int JournalHit()
    {
        int found = 0;
        for (int i = 0; i < OperationsPerInvoke; i++)
        {
            if (_byCell.TryGetValue(_present, out _)) found++;
        }
        return found;
    }

    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    public int IndexOnlyHit()
    {
        int found = 0;
        for (int i = 0; i < OperationsPerInvoke; i++)
        {
            if (_byIndex.TryGetValue(_index, out _)) found++;
        }
        return found;
    }
}

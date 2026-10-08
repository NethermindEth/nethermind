// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm.Benchmark;

/// <summary>
/// Measures one transaction on a reused <see cref="BalDataMeter"/>: <see cref="BalDataMeter.Reset"/> followed by
/// <see cref="Count"/> address or storage key accesses, either all to one entry or all to distinct entries.
/// </summary>
/// <remarks>8192 distinct storage keys is the most a transaction can meter under the EIP-7825 cap.</remarks>
[MemoryDiagnoser]
public class BalDataMeterBenchmarks
{
    [Params(16, 1024, 8192)]
    public int Count { get; set; }

    [Params(false, true)]
    public bool Distinct { get; set; }

    private Address[] _addresses = null!;
    private StorageCell[] _cells = null!;
    private BalDataMeter _meter = null!;

    [GlobalSetup]
    public void Setup()
    {
        _addresses = new Address[Count];
        _cells = new StorageCell[Count];
        Address contract = Address.FromNumber(0x1000);
        for (int i = 0; i < Count; i++)
        {
            int n = Distinct ? i : 0;
            _addresses[i] = Address.FromNumber(new UInt256(0x2000UL + (ulong)n));
            _cells[i] = new StorageCell(contract, (UInt256)n);
        }

        _meter = new BalDataMeter();
    }

    [Benchmark]
    public bool Addresses()
    {
        BalDataMeter meter = _meter.Reset(0, ulong.MaxValue);
        bool ok = true;
        foreach (Address address in _addresses)
        {
            ok &= meter.TryMeterAddress(address);
        }

        return ok;
    }

    [Benchmark]
    public bool StorageKeys()
    {
        BalDataMeter meter = _meter.Reset(0, ulong.MaxValue);
        bool ok = true;
        foreach (StorageCell cell in _cells)
        {
            ok &= meter.TryMeterStorageKey(cell);
        }

        return ok;
    }
}

/// <summary>
/// Measures a transaction touching <see cref="LargeCount"/> distinct storage keys followed by
/// <see cref="SmallTxs"/> transactions touching 16 each, on one reused <see cref="BalDataMeter"/>, to show the cost of
/// the trim in <see cref="BalDataMeter.Reset"/> and the regrowth it causes.
/// </summary>
[MemoryDiagnoser]
public class BalDataMeterResetBenchmarks
{
    private const int SmallCount = 16;
    private const int SmallTxs = 64;

    /// <summary>1024 stays below the trim threshold; 8192 exceeds it.</summary>
    [Params(1024, 8192)]
    public int LargeCount { get; set; }

    private StorageCell[] _cells = null!;
    private BalDataMeter _meter = null!;

    [GlobalSetup]
    public void Setup()
    {
        _cells = new StorageCell[LargeCount];
        Address contract = Address.FromNumber(0x1000);
        for (int i = 0; i < LargeCount; i++)
        {
            _cells[i] = new StorageCell(contract, (UInt256)i);
        }

        _meter = new BalDataMeter();
    }

    [Benchmark]
    public bool LargeThenSmall()
    {
        BalDataMeter meter = _meter.Reset(0, ulong.MaxValue);
        bool ok = true;
        foreach (StorageCell cell in _cells)
        {
            ok &= meter.TryMeterStorageKey(cell);
        }

        for (int tx = 0; tx < SmallTxs; tx++)
        {
            meter.Reset(0, ulong.MaxValue);
            for (int i = 0; i < SmallCount; i++)
            {
                ok &= meter.TryMeterStorageKey(_cells[i]);
            }
        }

        return ok;
    }
}

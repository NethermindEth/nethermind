// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm.Benchmark;

/// <summary>
/// Measures <see cref="PoppedAddressCache"/> over cyclic address working sets: 1 = hit path,
/// 2/4 = alternating sets that thrash a single-entry cache, 8 = over capacity.
/// </summary>
[MemoryDiagnoser]
public class PoppedAddressCacheBenchmarks
{
    private const int OpsPerInvoke = 8192;

    [Params(1, 2, 4, 8)]
    public int WorkingSet { get; set; }

    private byte[][] _addresses = null!;
    private PoppedAddressCache _cache = null!;

    [GlobalSetup]
    public void Setup()
    {
        _addresses = new byte[8][];
        for (int i = 0; i < 8; i++)
        {
            _addresses[i] = Address.FromNumber(new UInt256(0x1000UL + (ulong)i)).Bytes.ToArray();
        }

        _cache = new PoppedAddressCache();
    }

    [Benchmark(OperationsPerInvoke = OpsPerInvoke)]
    public Address CyclicPops()
    {
        Address last = null!;
        byte[][] addresses = _addresses;
        int mask = WorkingSet - 1;
        PoppedAddressCache cache = _cache;
        for (int i = 0; i < OpsPerInvoke; i++)
        {
            last = cache.GetOrCreate(addresses[i & mask]);
        }

        return last;
    }
}

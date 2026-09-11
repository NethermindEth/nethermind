// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using BenchmarkDotNet.Attributes;
using Nethermind.Core.BlockAccessLists;

namespace Nethermind.Benchmarks.State;

/// <summary>Measures the additional retained value graph when a block exceeds a 256-slot shared cache.</summary>
/// <remarks>
/// Every allocation made by the method remains reachable from its returned cache, so allocated bytes also
/// describe its additional retained graph. Common plan/worker structures and the shared zero singleton are excluded.
/// One quarter of slots are zero; ValueBytes = 0 makes all slots zero. CopyZeroValues models fallback reads
/// copying the parent's single-byte zero; otherwise zeros share the prefetch representation.
/// </remarks>
[MemoryDiagnoser]
public class BalOrdinalCacheAllocationBenchmarks
{
    [Params(4096)]
    public int Reads { get; set; }

    [Params(0, 1, 32)]
    public int ValueBytes { get; set; }

    [Params(false, true)]
    public bool CopyZeroValues { get; set; }

    [Benchmark]
    public BalStorageValueCache? CreateRetainedValues()
    {
        if (Reads <= 256) return null;
        BalStorageValueCache cache = new(Reads);
        for (int i = 0; i < Reads; i++)
        {
            byte[] value = [];
            if (ValueBytes != 0 && i % 4 != 0)
            {
                value = new byte[ValueBytes];
                value[0] = 42;
            }
            else if (CopyZeroValues)
            {
                value = new byte[1];
            }
            cache.Set(i, value);
        }
        return cache;
    }
}

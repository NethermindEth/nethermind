// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Benchmarks.State;

[DisassemblyDiagnoser]
public class StorageValueMapBenchmark
{
    private StorageValueMap _map;

    private static readonly StorageValue Value = new(new UInt256(1));

    [GlobalSetup]
    public void Setup()
    {
        _map = new StorageValueMap(1024);
    }

    [Benchmark]
    public void Map()
    {
        _map.Map(Value);
    }
}

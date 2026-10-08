// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.State.Repositories;

namespace Nethermind.Benchmarks.Store;

/// <summary>
/// Number to level lookups from every core at once over a window of levels, the access pattern of
/// <c>eth_getBlockByNumber</c> under load: a window wider than the level cache turns every lookup into a db read
/// and a cache insert.
/// </summary>
[MemoryDiagnoser]
public class ChainLevelInfoRepositoryBenchmark
{
    private const int LookupsPerWorker = 4096;

    private ChainLevelInfoRepository _repository = null!;
    private ulong[] _numbers = null!;

    [Params(32, 200)]
    public int DistinctLevels { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        MemDb db = new();
        ChainLevelInfoRepository writer = new(db);
        Random random = new(42);
        _numbers = new ulong[DistinctLevels];
        for (int i = 0; i < _numbers.Length; i++)
        {
            ulong number = (ulong)random.NextInt64(24_600_000, 25_490_000);
            _numbers[i] = number;
            writer.PersistLevel(number, new ChainLevelInfo(true, new BlockInfo(TestItem.KeccakA, number)));
        }

        _repository = new ChainLevelInfoRepository(db);
    }

    [Benchmark(OperationsPerInvoke = LookupsPerWorker)]
    public void LoadLevel_from_every_core() =>
        Parallel.For(0, Environment.ProcessorCount, worker =>
        {
            ulong[] numbers = _numbers;
            int index = worker * 7919;
            for (int i = 0; i < LookupsPerWorker; i++)
            {
                _repository.LoadLevel(numbers[(index + i) % numbers.Length]);
            }
        });
}

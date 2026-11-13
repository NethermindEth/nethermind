// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.State.Repositories;
using Testably.Abstractions;

namespace Nethermind.Benchmarks.Store;

[MemoryDiagnoser]
public class ChainLevelBatchReadBenchmark
{
    private TempPath _directory = null!;
    private DbOnTheRocks _db = null!;
    private IContainer _container = null!;
    private IChainLevelInfoRepository _repository = null!;
    private ulong[] _numbers = null!;

    [Params(1, 2, 32, 256)]
    public int Count { get; set; }

    [Params(0, 50, 100)]
    public int HitPercent { get; set; }

    [Params(false, true)]
    public bool MixedLengths { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _directory = TempPath.GetTempDirectory();
        using (IContainer configs = new ContainerBuilder().AddModule(new TestNethermindModule()).Build())
        {
            _db = (DbOnTheRocks)new RocksDbFactory(configs.Resolve<IRocksDbConfigFactory>(), configs.Resolve<IDbConfig>(),
                    new HyperClockCacheWrapper(), new RealFileSystem(), LimboLogs.Instance, _directory.Path)
                .CreateDb(new DbSettings(DbNames.BlockInfos, "levels"));
        }

        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddKeyedSingleton<IDb>(DbNames.BlockInfos, _db)
            .Build();
        _repository = _container.Resolve<IChainLevelInfoRepository>();
        _numbers = new ulong[Count];
        int hits = Count * HitPercent / 100;
        for (int i = 0; i < Count; i++)
        {
            ulong number = MixedLengths && (i & 1) != 0 ? (ulong)i + 1 : 25_000_000UL + (ulong)i;
            _numbers[i] = number;
            if (i < hits)
                _repository.PersistLevel(number, new ChainLevelInfo(true, new BlockInfo(TestItem.KeccakA, number)));
        }
        _db.Flush();

        using ArrayPoolListRef<ulong> numbers = new(_numbers);
        using IOwnedReadOnlyList<ChainLevelInfo> levels = _repository.MultiLoadLevel(numbers);
        if (levels.Count != Count) throw new InvalidOperationException("Wrong batch result count.");
        for (int i = 0; i < Count; i++)
        {
            ChainLevelInfo level = levels[i];
            if (i >= hits)
            {
                if (level is not null) throw new InvalidOperationException("Missing level returned a value.");
            }
            else if (level is null || !level.HasBlockOnMainChain || level.BlockInfos.Length != 1
                || level.BlockInfos[0].BlockHash != TestItem.KeccakA
                || level.BlockInfos[0].TotalDifficulty != _numbers[i])
            {
                throw new InvalidOperationException("Wrong decoded level or request order.");
            }
        }
    }

    [Benchmark]
    public int ReadLevels()
    {
        using ArrayPoolListRef<ulong> numbers = new(_numbers);
        using IOwnedReadOnlyList<ChainLevelInfo> levels = _repository.MultiLoadLevel(numbers);
        return levels.Count;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _container.Dispose();
        _db.Dispose();
        _directory.Dispose();
    }
}

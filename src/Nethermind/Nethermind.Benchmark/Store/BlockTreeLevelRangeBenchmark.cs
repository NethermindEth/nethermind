// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Testably.Abstractions;

namespace Nethermind.Benchmarks.Store;

/// <summary>
/// Level-range updates of <see cref="BlockTree"/> over a RocksDB block-info column with a cold level cache.
/// </summary>
[MemoryDiagnoser]
public class BlockTreeLevelRangeBenchmark
{
    // Keeps the measured levels at one key length, as on a live chain; levels below 256 have one-byte keys.
    private const int LongKeyLevels = 257;

    private TempPath _directory = null!;
    private DbOnTheRocks _db = null!;
    private BlockTree _blockTree = null!;
    private IClearableCache _levelCache = null!;
    private BlockHeader _head = null!;
    private BlockHeader _reorgTarget = null!;
    private List<BlockInfo> _beaconInfos = null!;
    private ulong _beaconClearStart;

    [Params(64, 1024)]
    public int Levels { get; set; }

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

        BlockTreeBuilder builder = Build.A.BlockTree().WithBlockInfoDb(_db).OfChainLength(2 * Levels + LongKeyLevels);
        _blockTree = builder.TestObject;
        _levelCache = (IClearableCache)builder.ChainLevelInfoRepository;
        _db.Flush();

        _head = _blockTree.Head!.Header;
        _reorgTarget = _blockTree.FindHeader(_head.Number - (ulong)Levels, BlockTreeLookupOptions.RequireCanonical)!;

        _beaconClearStart = _head.Number;
        _beaconInfos = new List<BlockInfo>(Levels);
        for (ulong number = _reorgTarget.Number - (ulong)Levels + 1; number <= _reorgTarget.Number; number++)
        {
            BlockHeader header = _blockTree.FindHeader(number, BlockTreeLookupOptions.RequireCanonical)!;
            BlockInfo info = _blockTree.GetInfo(number, header.Hash!).Info!;
            info.BlockNumber = number;
            _beaconInfos.Add(info);
        }
    }

    [IterationSetup(Target = nameof(Reorg))]
    public void RestoreHead()
    {
        if (!_blockTree.TryUpdateMainChain(_head, true, true)) throw new InvalidOperationException("Head restore failed.");
        _levelCache.ClearCache();
    }

    [Benchmark]
    public void Reorg()
    {
        if (!_blockTree.TryUpdateMainChain(_reorgTarget, true, true)) throw new InvalidOperationException("Reorg failed.");
    }

    [Benchmark]
    public void BeaconMainChain()
    {
        _levelCache.ClearCache();
        _blockTree.UpdateBeaconMainChain(_beaconInfos, _beaconClearStart);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _db.Dispose();
        _directory.Dispose();
    }
}

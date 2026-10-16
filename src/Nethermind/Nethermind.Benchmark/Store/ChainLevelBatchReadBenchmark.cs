// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.State.Repositories;

namespace Nethermind.Benchmarks.Store;

[MemoryDiagnoser]
public class ChainLevelBatchReadBenchmark
{
    private TempPath _directory = null!;
    private DbOnTheRocks _db = null!;
    private ChainLevelInfoRepository _repository = null!;
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
        DbConfig config = new();
        RocksDbConfigFactory factory = new(config, new PruningConfig(), new TestHardwareInfo(1.GiB),
            LimboLogs.Instance, validateConfig: false);
        _db = new DbOnTheRocks(_directory.Path, new DbSettings(DbNames.BlockInfos, "levels"),
            config, factory, LimboLogs.Instance);
        _repository = new ChainLevelInfoRepository(_db);
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
        _db.Dispose();
        _directory.Dispose();
    }
}

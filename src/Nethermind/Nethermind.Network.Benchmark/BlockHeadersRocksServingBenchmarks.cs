// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Headers;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.History;
using Nethermind.Logging;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Specs.Forks;
using Nethermind.State.Repositories;
using Nethermind.Synchronization;
using NSubstitute;
using Testably.Abstractions;

namespace Nethermind.Network.Benchmarks;

/// <summary>
/// Measures finding the headers of one <c>GetBlockHeaders</c> request on RocksDB header and block-info columns, with
/// the header and level caches cleared before each request.
/// </summary>
[MemoryDiagnoser]
public class BlockHeadersRocksServingBenchmarks
{
    private const int MaxCount = 1024;
    private const int MaxSkip = 3;

    // Keeps the served levels at one key length, as on a live chain; levels below 256 have one-byte keys.
    private const int LongKeyLevels = 256;

    private TempPath _directory = null!;
    private IDb _headersDb = null!;
    private IDb _blockInfosDb = null!;
    private IContainer _container = null!;
    private ISyncServer _server = null!;
    private IClearableCache _headerCache = null!;
    private IClearableCache _levelCache = null!;
    private Hash256 _startHash = null!;

    [Params(192, 1024)]
    public int Count { get; set; }

    [Params(0, MaxSkip)]
    public int Skip { get; set; }

    [Params(false, true)]
    public bool Reverse { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _directory = TempPath.GetTempDirectory();
        using (IContainer configs = new ContainerBuilder().AddModule(new TestNethermindModule()).Build())
        {
            RocksDbFactory factory = new(configs.Resolve<IRocksDbConfigFactory>(), configs.Resolve<IDbConfig>(),
                new HyperClockCacheWrapper(), new RealFileSystem(), LimboLogs.Instance, _directory.Path);
            _headersDb = factory.CreateDb(new DbSettings(DbNames.Headers, "headers"));
            _blockInfosDb = factory.CreateDb(new DbSettings(DbNames.BlockInfos, "blockInfos"));
        }

        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(Prague.Instance))
            .AddKeyedSingleton<IDb>(DbNames.Headers, _headersDb)
            .AddKeyedSingleton<IDb>(DbNames.BlockInfos, _blockInfosDb)
            .AddSingleton<IHistoryPruner>(Substitute.For<IHistoryPruner>())
            .AddSingleton<IBlockValidator>(Always.Valid)
            .AddSingleton<ISealValidator>(Always.Valid)
            .Build();
        IBlockTree blockTree = _container.Resolve<IBlockTree>();

        Block parent = Build.A.Block.Genesis.TestObject;
        blockTree.SuggestBlock(parent);
        int chainLength = LongKeyLevels + MaxCount * (MaxSkip + 1) + 1;
        for (int i = 0; i < chainLength; i++)
        {
            Block block = Build.A.Block
                .WithParent(parent)
                .WithBaseFeePerGas(7)
                .WithWithdrawalsRoot(TestItem.KeccakA)
                .WithBlobGasUsed(131072)
                .WithExcessBlobGas(0)
                .WithParentBeaconBlockRoot(TestItem.KeccakB)
                .WithRequestsHash(TestItem.KeccakC)
                .TestObject;
            blockTree.SuggestBlock(block);
            blockTree.TryUpdateMainChain(block.Header, true, false, block);
            parent = block;
        }

        _headersDb.Flush();
        _blockInfosDb.Flush();

        ulong span = (ulong)(Count - 1) * (ulong)(Skip + 1);
        _startHash = blockTree.FindHash((ulong)LongKeyLevels + (Reverse ? span + 1 : 1))!;
        _headerCache = (IClearableCache)_container.Resolve<IHeaderStore>();
        _levelCache = (IClearableCache)_container.Resolve<IChainLevelInfoRepository>();
        _server = _container.Resolve<ISyncServer>();

        using IOwnedReadOnlyList<byte> headers = _server.FindHeadersRlp(_startHash, Count, Skip, Reverse);
        if (new RlpReader(headers.AsSpan()).CountItems() != Count)
            throw new InvalidOperationException("The request must find every header.");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _container.Dispose();
        _headersDb.Dispose();
        _blockInfosDb.Dispose();
        _directory.Dispose();
    }

    [Benchmark]
    public int FindHeadersRlp()
    {
        _headerCache.ClearCache();
        _levelCache.ClearCache();
        using IOwnedReadOnlyList<byte> headers = _server.FindHeadersRlp(_startHash, Count, Skip, Reverse);
        return headers.Count;
    }
}

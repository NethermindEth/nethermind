// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Facade.Filters;
using Nethermind.Facade.Filters.Topics;
using Nethermind.Facade.Find;
using Nethermind.Logging;
using Nethermind.Specs;
using Testably.Abstractions;

namespace Nethermind.JsonRpc.Benchmark;

/// <summary>
/// <c>eth_getLogs</c> over a range of historical blocks without a log index, on RocksDB header and block-info columns,
/// with a filter no block bloom matches: the cost is reading each canonical header and testing its bloom.
/// </summary>
[MemoryDiagnoser]
public class LogFinderRocksScanBenchmarks
{
    private const int BlocksAboveRange = 600;

    private TempPath _directory = null!;
    private IDb _headersDb = null!;
    private IDb _blockInfosDb = null!;
    private LogFinder _logFinder = null!;
    private LogFilter _filter = null!;

    [Params(1000, 10_000)]
    public int Blocks { get; set; }

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

        BlockTreeBuilder builder = Build.A.BlockTree().WithHeadersDb(_headersDb).WithBlockInfoDb(_blockInfosDb);
        int chainLength = Blocks + BlocksAboveRange;
        List<BlockHeader> headers = [with(chainLength)];
        BlockHeader parent = Build.A.BlockHeader.WithNumber(0).TestObject;
        headers.Add(parent);
        for (int i = 1; i < chainLength; i++)
        {
            parent = Build.A.BlockHeader.WithParent(parent).TestObject;
            headers.Add(parent);
        }

        builder.HeaderStore.BulkInsert(headers);
        foreach (BlockHeader header in headers)
        {
            builder.ChainLevelInfoRepository.PersistLevel(header.Number, new ChainLevelInfo(true, new BlockInfo(header.Hash!, header.TotalDifficulty ?? 0) { WasProcessed = true }));
        }

        _headersDb.Flush();
        _blockInfosDb.Flush();

        BlockTree blockTree = builder.TestObject;
        Block head = Build.A.Block.WithParent(parent).TestObject;
        blockTree.SuggestBlock(head);
        blockTree.TryUpdateMainChain(head.Header, true, false, head);

        InMemoryReceiptStorage receiptStorage = new();
        _logFinder = new LogFinder(blockTree, receiptStorage, receiptStorage, NullLogManager.Instance,
            new ReceiptsRecovery(new EthereumEcdsa(BlockchainIds.Mainnet), MainnetSpecProvider.Instance), MainnetSpecProvider.Instance);
        _filter = new LogFilter(0, new BlockParameter(1UL), new BlockParameter((ulong)Blocks), new AddressFilter(TestItem.AddressA), new SequenceTopicsFilter());
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _headersDb.Dispose();
        _blockInfosDb.Dispose();
        _directory.Dispose();
    }

    [Benchmark]
    public int Scan() => _logFinder.FindLogs(_filter).Count();
}

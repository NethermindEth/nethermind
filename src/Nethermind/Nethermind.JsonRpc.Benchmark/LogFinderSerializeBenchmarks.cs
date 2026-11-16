// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Headers;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Facade.Filters;
using Nethermind.Facade.Filters.Topics;
using Nethermind.Facade.Find;
using Nethermind.Logging;
using Nethermind.Specs;

namespace Nethermind.JsonRpc.Benchmark;

[MemoryDiagnoser]
public class LogFinderSerializeBenchmarks
{
    private const int ChainLength = 1200;

    private LogFinder _logFinder = null!;
    private LogFilter _filter = null!;
    private BlockHeader _from = null!;
    private BlockHeader _to = null!;

    [Params(64, 512)]
    public int Range { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        InMemoryReceiptStorage receiptStorage = new();
        BlockTreeBuilder builder = Build.A.BlockTree().WithHeaderStore(new HeaderStore(new MemDb(), new MemDb()));
        builder.BlockInfoDb = new MemDb();
        builder.BlocksDb = new MemDb();
        IBlockTree blockTree = builder
            .WithTransactions(receiptStorage, Logs)
            .OfChainLength(ChainLength)
            .TestObject;

        _logFinder = new LogFinder(blockTree, receiptStorage, receiptStorage, NullLogManager.Instance,
            new ReceiptsRecovery(new EthereumEcdsa(blockTree.ChainId), MainnetSpecProvider.Instance), MainnetSpecProvider.Instance);
        _filter = new LogFilter(0, BlockParameter.Earliest, BlockParameter.Latest, new AddressFilter(TestItem.AddressA), SequenceTopicsFilter.AnyTopic);
        _to = blockTree.Head!.Header;
        _from = blockTree.FindHeader(_to.Number - (ulong)Range + 1)!;
    }

    [Benchmark]
    public int Serialize()
    {
        int count = 0;
        foreach (SerializedLogs logs in FoundLogs.Serialize(_logFinder.FindLogs(_filter, _from, _to)))
        {
            count += logs.Count;
            logs.Dispose();
        }

        return count;
    }

    private static IEnumerable<LogEntry> Logs(Block block, Transaction transaction)
    {
        yield return Build.A.LogEntry.WithAddress(TestItem.AddressA).WithTopics(TestItem.KeccakA).WithData([1, 2, 3]).TestObject;
        yield return Build.A.LogEntry.WithAddress(TestItem.AddressA).WithTopics(TestItem.KeccakA, TestItem.KeccakB).TestObject;
    }
}

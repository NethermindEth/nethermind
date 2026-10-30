// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Filters;
using Nethermind.Facade.Filters.Topics;
using Nethermind.Facade.Find;
using Nethermind.Logging;
using Nethermind.Specs;

namespace Nethermind.JsonRpc.Benchmark;

/// <summary>
/// Scanning a non-indexed <c>eth_getLogs</c> range on the parallel path, with a filter that matches few blocks or many.
/// </summary>
[MemoryDiagnoser]
public class LogFinderBenchmarks
{
    private const int ChainLength = 5000;
    private const int FewLogsBlockInterval = 250;

    private LogFinder _logFinder = null!;
    private IBlockTree _blockTree = null!;
    private LogFilter _filter = null!;
    private BlockHeader _from = null!;
    private BlockHeader _to = null!;

    [Params(50, 500, 5000)]
    public int Range { get; set; }

    [Params(false, true)]
    public bool ManyLogs { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        InMemoryReceiptStorage receiptStorage = new();
        _blockTree = Build.A.BlockTree()
            .WithTransactions(receiptStorage, Logs)
            .OfChainLength(ChainLength)
            .TestObject;

        _logFinder = new LogFinder(_blockTree, receiptStorage, receiptStorage, NullLogManager.Instance,
            new ReceiptsRecovery(new Crypto.EthereumEcdsa(_blockTree.ChainId), MainnetSpecProvider.Instance),
            MainnetSpecProvider.Instance);

        Address address = ManyLogs ? TestItem.AddressA : TestItem.AddressB;
        _filter = new LogFilter(0, BlockParameter.Earliest, BlockParameter.Latest, new AddressFilter(address), SequenceTopicsFilter.AnyTopic);
        _to = _blockTree.Head!.Header;
        _from = _blockTree.FindHeader(_to.Number - (ulong)Range + 1)!;
    }

    [Benchmark]
    public int FindLogs()
    {
        int count = 0;
        foreach (FilterLog _ in _logFinder.FindLogs(_filter, _from, _to))
        {
            count++;
        }

        return count;
    }

    private static IEnumerable<LogEntry> Logs(Block block, Transaction transaction)
    {
        for (int i = 0; i < 5; i++)
        {
            yield return Build.A.LogEntry.WithAddress(TestItem.AddressA).WithTopics(TestItem.KeccakA).TestObject;
        }

        if (block.Number % FewLogsBlockInterval < 3)
        {
            yield return Build.A.LogEntry.WithAddress(TestItem.AddressB).WithTopics(TestItem.KeccakB).TestObject;
        }
    }
}

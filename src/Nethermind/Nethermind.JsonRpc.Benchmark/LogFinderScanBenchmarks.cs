// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Facade.Filters;
using Nethermind.Facade.Filters.Topics;
using Nethermind.Facade.Find;
using Nethermind.Logging;
using Nethermind.Specs;

namespace Nethermind.JsonRpc.Benchmark;

/// <summary>
/// <c>eth_getLogs</c> over a range of historical blocks without a log index, with a filter no block bloom matches: the
/// cost is reading each canonical header and testing its bloom.
/// </summary>
[MemoryDiagnoser]
public class LogFinderScanBenchmarks
{
    private const int BlocksAboveRange = 600;

    private LogFinder _logFinder = null!;
    private LogFilter _filter = null!;

    [Params(1000, 4000)]
    public int Blocks { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(Blocks + BlocksAboveRange).TestObject;
        InMemoryReceiptStorage receiptStorage = new();
        _logFinder = new LogFinder(blockTree, receiptStorage, receiptStorage, NullLogManager.Instance,
            new ReceiptsRecovery(new EthereumEcdsa(BlockchainIds.Mainnet), MainnetSpecProvider.Instance), MainnetSpecProvider.Instance);
        _filter = new LogFilter(0, new BlockParameter(1UL), new BlockParameter((ulong)Blocks), new AddressFilter(TestItem.AddressA), new SequenceTopicsFilter());
    }

    [Benchmark]
    public int Scan() => _logFinder.FindLogs(_filter).Count();
}

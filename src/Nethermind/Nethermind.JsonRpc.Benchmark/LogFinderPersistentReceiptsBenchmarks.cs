// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Facade.Filters;
using Nethermind.Facade.Filters.Topics;
using Nethermind.Facade.Find;
using Nethermind.Logging;
using Nethermind.Specs;

namespace Nethermind.JsonRpc.Benchmark;

/// <summary>
/// <c>eth_getLogs</c> for one block whose receipts are read from the receipts database, so the log fields come from
/// recovery: from the stored transaction hashes, or from the block body when the receipts do not store them.
/// </summary>
[MemoryDiagnoser]
public class LogFinderPersistentReceiptsBenchmarks
{
    private LogFinder _logFinder = null!;
    private LogFilter _filter = null!;

    [Params(200)]
    public int Transactions { get; set; }

    [Params(false, true)]
    public bool StoreTxHashes { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        BlockTreeBuilder builder = Build.A.BlockTree();
        builder.BlocksDb = new MemDb();
        BlockTree blockTree = builder.OfChainLength(1).TestObject;

        LogEntry[] logs =
        [
            Build.A.LogEntry.WithAddress(TestItem.AddressA).WithTopics(TestItem.KeccakA).WithData([1, 2, 3]).TestObject,
            Build.A.LogEntry.WithAddress(TestItem.AddressA).WithTopics(TestItem.KeccakA, TestItem.KeccakB).TestObject
        ];
        Transaction[] transactions = new Transaction[Transactions];
        for (int i = 0; i < transactions.Length; i++)
        {
            transactions[i] = Build.A.Transaction.WithNonce((ulong)i).SignedAndResolved().TestObject;
        }

        Block block = Build.A.Block.WithParent(blockTree.Head!).WithTransactions(transactions).WithBloom(new Bloom(logs)).TestObject;
        blockTree.SuggestBlock(block);
        TxReceipt[] receipts = new TxReceipt[transactions.Length];
        for (int i = 0; i < receipts.Length; i++)
        {
            receipts[i] = Build.A.Receipt.WithLogs(logs).WithTransactionHash(transactions[i].Hash).WithBlockHash(block.Hash).WithBlockNumber(block.Number).TestObject;
        }

        ReceiptsRecovery recovery = new(new EthereumEcdsa(blockTree.ChainId), MainnetSpecProvider.Instance);
        // A fresh block store has no block cache, so recovery reads the stored block body.
        PersistentReceiptStorage receiptStorage = new(new TestMemColumnsDb<ReceiptsColumns>(), MainnetSpecProvider.Instance, recovery,
            blockTree, new BlockStore(builder.BlocksDb), new ReceiptConfig { StoreTxHashes = StoreTxHashes })
        { MigratedBlockNumber = 0 };
        receiptStorage.Insert(block, receipts, ensureCanonical: false);
        receiptStorage.ClearCache();

        _logFinder = new LogFinder(blockTree, receiptStorage, receiptStorage, NullLogManager.Instance, recovery, MainnetSpecProvider.Instance);
        BlockParameter blockParameter = new(block.Hash!);
        _filter = new LogFilter(0, blockParameter, blockParameter, new AddressFilter(TestItem.AddressA), SequenceTopicsFilter.AnyTopic);
    }

    [Benchmark]
    public int Serialize()
    {
        int count = 0;
        foreach (SerializedLogs logs in FoundLogs.Serialize(_logFinder.FindLogs(_filter)))
        {
            count += logs.Count;
            logs.Dispose();
        }

        return count;
    }

    [Benchmark]
    public int FindLogs()
    {
        int count = 0;
        foreach (FilterLog _ in _logFinder.FindLogs(_filter))
        {
            count++;
        }

        return count;
    }
}

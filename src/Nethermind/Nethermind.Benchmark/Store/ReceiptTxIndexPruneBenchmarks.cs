// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Reflection;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Specs;
using NSubstitute;

namespace Nethermind.Benchmarks.Store;

/// <summary>
/// The tx-index prune that runs on each new head: drops the index entries of the block that leaves the lookup window,
/// hashing its transactions from the stored body.
/// </summary>
[MemoryDiagnoser]
public class ReceiptTxIndexPruneBenchmarks
{
    private const ulong TxLookupLimit = 1;

    private Action<Block> _pruneOldTxIndex = null!;
    private Block _newMain = null!;

    [Params(200)]
    public int Transactions { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        Transaction[] transactions = new Transaction[Transactions];
        for (int i = 0; i < transactions.Length; i++)
        {
            transactions[i] = Build.A.Transaction.WithNonce((ulong)i).SignedAndResolved().TestObject;
        }

        Block oldBlock = Build.A.Block.WithNumber(1).WithTransactions(transactions).TestObject;
        BlockStore blockStore = new(new MemDb());
        blockStore.Insert(oldBlock);

        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindBlockHash(oldBlock.Number).Returns(oldBlock.Hash);
        _newMain = Build.A.Block.WithNumber(oldBlock.Number + TxLookupLimit).TestObject;

        PersistentReceiptStorage storage = new(new TestMemColumnsDb<ReceiptsColumns>(), MainnetSpecProvider.Instance,
            new ReceiptsRecovery(new EthereumEcdsa(BlockchainIds.Mainnet), MainnetSpecProvider.Instance), blockTree, blockStore,
            new ReceiptConfig { TxLookupLimit = TxLookupLimit });
        _pruneOldTxIndex = typeof(PersistentReceiptStorage)
            .GetMethod("PruneOldTxIndex", BindingFlags.NonPublic | BindingFlags.Instance)!
            .CreateDelegate<Action<Block>>(storage);
    }

    [Benchmark]
    public void PruneOldTxIndex() => _pruneOldTxIndex(_newMain);
}

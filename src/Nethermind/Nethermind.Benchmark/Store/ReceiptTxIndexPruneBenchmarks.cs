// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Reflection;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
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

    private IContainer _container = null!;
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

        ContainerBuilder builder = new();
        builder.AddModule(new TestNethermindModule(new ReceiptConfig { TxLookupLimit = TxLookupLimit, DeferredPersistence = false }));
        builder.AddSingleton<ISpecProvider>(MainnetSpecProvider.Instance);
        builder.AddSingleton<IBlockTree>(blockTree);
        builder.AddSingleton<IBlockStore>(blockStore);
        builder.AddSingleton<IColumnsDb<ReceiptsColumns>>(new TestMemColumnsDb<ReceiptsColumns>());
        _container = builder.Build();

        PersistentReceiptStorage storage = (PersistentReceiptStorage)_container.Resolve<IReceiptStorage>();
        _pruneOldTxIndex = typeof(PersistentReceiptStorage)
            .GetMethod("PruneOldTxIndex", BindingFlags.NonPublic | BindingFlags.Instance)!
            .CreateDelegate<Action<Block>>(storage);
    }

    [GlobalCleanup]
    public void Cleanup() => _container.Dispose();

    [Benchmark]
    public void PruneOldTxIndex() => _pruneOldTxIndex(_newMain);
}

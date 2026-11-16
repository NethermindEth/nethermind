// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Testably.Abstractions;

namespace Nethermind.Benchmarks.Store;

/// <summary>
/// Block receipts and transaction-index reads from a RocksDB receipts database whose values are in the block cache.
/// </summary>
[MemoryDiagnoser]
public class ReceiptStorageReadBenchmarks
{
    private const int Blocks = 32;

    private TempPath _directory = null!;
    private IColumnsDb<ReceiptsColumns> _receiptsDb = null!;
    private IContainer _container = null!;
    private IReceiptStorage _storage = null!;
    private Block[] _blocks = null!;
    private Hash256[] _txHashes = null!;

    [Params(10, 200)]
    public int TransactionsPerBlock { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _directory = TempPath.GetTempDirectory();
        using (IContainer configs = new ContainerBuilder().AddModule(new TestNethermindModule()).Build())
        {
            _receiptsDb = new RocksDbFactory(configs.Resolve<IRocksDbConfigFactory>(), configs.Resolve<IDbConfig>(),
                    new HyperClockCacheWrapper(), new RealFileSystem(), LimboLogs.Instance, _directory.Path)
                .CreateColumnsDb<ReceiptsColumns>(new DbSettings(DbNames.Receipts, DbNames.Receipts));
        }

        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new ReceiptConfig { DeferredPersistence = false }))
            .AddSingleton<ISpecProvider>(new TestSpecProvider(Cancun.Instance))
            .AddSingleton<IColumnsDb<ReceiptsColumns>>(_receiptsDb)
            .Build();
        IBlockTree blockTree = _container.Resolve<IBlockTree>();
        _storage = _container.Resolve<IReceiptStorage>();

        Block genesis = Build.A.Block.Genesis.TestObject;
        blockTree.SuggestBlock(genesis);
        blockTree.TryUpdateMainChain(genesis.Header, true, false, genesis);

        _blocks = new Block[Blocks];
        _txHashes = new Hash256[Blocks * TransactionsPerBlock];
        Block parent = blockTree.Head!;
        ulong nonce = 0;
        for (int b = 0; b < Blocks; b++)
        {
            Transaction[] transactions = new Transaction[TransactionsPerBlock];
            TxReceipt[] receipts = new TxReceipt[TransactionsPerBlock];
            for (int i = 0; i < transactions.Length; i++)
            {
                transactions[i] = Build.A.Transaction.WithNonce(nonce++).TestObject;
                transactions[i].Hash = transactions[i].CalculateHash();
                _txHashes[b * TransactionsPerBlock + i] = transactions[i].Hash!;
                receipts[i] = new TxReceipt
                {
                    TxHash = transactions[i].Hash,
                    Index = i,
                    GasUsedTotal = (ulong)(i + 1) * 50_000,
                    Sender = TestItem.AddressA,
                    StatusCode = StatusCode.Success,
                    Logs =
                    [
                        Build.A.LogEntry.WithAddress(TestItem.AddressB).WithTopics(TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC).WithData(new byte[64]).TestObject,
                        Build.A.LogEntry.WithAddress(TestItem.AddressC).WithTopics(TestItem.KeccakD, TestItem.KeccakE).WithData(new byte[32]).TestObject
                    ]
                };
                receipts[i].Bloom = receipts[i].CalculateBloom();
            }

            Block block = Build.A.Block
                .WithParent(parent)
                .WithTransactions(transactions)
                .WithReceiptsRoot(TestItem.KeccakA)
                .TestObject;
            blockTree.SuggestBlock(block);
            blockTree.TryUpdateMainChain(block.Header, true, false, block);
            _storage.Insert(block, receipts);
            _blocks[b] = block;
            parent = block;
        }

        _receiptsDb.Flush();

        foreach (Block block in _blocks)
        {
            if (_storage.Get(block, recover: false).Length != TransactionsPerBlock)
                throw new InvalidOperationException($"Block {block.Number} does not return its {TransactionsPerBlock} receipts.");
        }

        for (int i = 0; i < _txHashes.Length; i++)
        {
            if (_storage.FindBlockHash(_txHashes[i]) != _blocks[i / TransactionsPerBlock].Hash)
                throw new InvalidOperationException($"Transaction {i} does not resolve to its block.");
        }
    }

    [Benchmark]
    public int GetBlockReceipts()
    {
        int count = 0;
        foreach (Block block in _blocks)
        {
            count += _storage.Get(block, recover: false).Length;
        }

        return count;
    }

    [Benchmark]
    public int FindTransactionBlock()
    {
        int found = 0;
        foreach (Hash256 txHash in _txHashes)
        {
            if (_storage.FindBlockHash(txHash) is not null) found++;
        }

        return found;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _container.Dispose();
        _receiptsDb.Dispose();
        _directory.Dispose();
    }
}

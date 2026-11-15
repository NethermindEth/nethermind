// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Encoding;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Testably.Abstractions;

namespace Nethermind.Benchmarks.Store;

/// <summary>
/// Receipts of one sync response, highest block first, inserted into a RocksDB receipts database as the receipts feed does:
/// decoded, or kept in their network encoding.
/// </summary>
[MemoryDiagnoser]
public class ReceiptsSyncInsertBenchmarks
{
    private const int BlocksPerResponse = 128;

    private TempPath _directory = null!;
    private IColumnsDb<ReceiptsColumns> _receiptsDb = null!;
    private IContainer _container = null!;
    private IReceiptStorage _storage = null!;
    private Block[] _blocks = null!;
    private TxReceipt[][] _receipts = null!;
    private Block[] _blocksWithEncodedTransactions = null!;
    private byte[][] _networkReceipts = null!;

    [Params(10, 100)]
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
            .AddModule(new TestNethermindModule(Cancun.Instance))
            .AddSingleton(_receiptsDb)
            .Build();
        _storage = _container.Resolve<IReceiptStorage>();

        BlockDecoder blockDecoder = new();
        ReceiptMessageDecoder receiptDecoder = new();
        _blocks = new Block[BlocksPerResponse];
        _receipts = new TxReceipt[BlocksPerResponse][];
        _blocksWithEncodedTransactions = new Block[BlocksPerResponse];
        _networkReceipts = new byte[BlocksPerResponse][];
        ulong nonce = 0;
        for (int b = 0; b < BlocksPerResponse; b++)
        {
            Transaction[] transactions = new Transaction[TransactionsPerBlock];
            TxReceipt[] receipts = new TxReceipt[TransactionsPerBlock];
            byte[][] encodedReceipts = new byte[TransactionsPerBlock][];
            for (int i = 0; i < transactions.Length; i++)
            {
                transactions[i] = Build.A.Transaction.WithNonce(nonce++).TestObject;
                transactions[i].Hash = transactions[i].CalculateHash();
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
                encodedReceipts[i] = receiptDecoder.Encode(receipts[i], RlpBehaviors.Eip658Receipts);
            }

            _blocks[b] = Build.A.Block
                .WithNumber((ulong)(1_000_000 - b))
                .WithTransactions(transactions)
                .WithReceiptsRoot(TestItem.KeccakA)
                .TestObject;
            _receipts[b] = receipts;
            _blocksWithEncodedTransactions[b] = new Block(_blocks[b].Header,
                new BlockBody(BlockBodyDecoder.DecodeEncodedTransactionsOfBlock(blockDecoder.Encode(_blocks[b])), []));
            _networkReceipts[b] = RlpTestHelper.EncodeSequence(encodedReceipts);
        }

        InsertBlocks();
        CheckStored();
        InsertEncodedBlocks();
        CheckStored();
    }

    private void CheckStored()
    {
        foreach (Block block in _blocks)
        {
            if (!_storage.HasBlock(block.Number, block.Hash!) || _storage.Get(block, recover: false).Length != TransactionsPerBlock)
                throw new InvalidOperationException($"Block {block.Number} was not stored with its {TransactionsPerBlock} receipts.");
        }

        if (_storage.MigratedBlockNumber != _blocks[^1].Number)
            throw new InvalidOperationException("The migrated block number does not point at the lowest inserted block.");
    }

    [Benchmark]
    public ulong InsertBlocks()
    {
        _storage.MigratedBlockNumber = ulong.MaxValue;
        for (int i = 0; i < _blocks.Length; i++)
        {
            _storage.Insert(_blocks[i], _receipts[i], ensureCanonical: true);
        }

        return _storage.MigratedBlockNumber;
    }

    [Benchmark]
    public ulong InsertEncodedBlocks()
    {
        _storage.MigratedBlockNumber = ulong.MaxValue;
        for (int i = 0; i < _blocksWithEncodedTransactions.Length; i++)
        {
            if (!_storage.TryInsertNetworkReceipts(_blocksWithEncodedTransactions[i], _networkReceipts[i], withBloom: true, ensureCanonical: true))
                throw new InvalidOperationException("The receipt storage does not take encoded receipts.");
        }

        return _storage.MigratedBlockNumber;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _container.Dispose();
        _receiptsDb.Dispose();
        _directory.Dispose();
    }
}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Facade;
using Nethermind.Specs;
using Nethermind.Specs.Forks;

namespace Nethermind.JsonRpc.Benchmark;

/// <summary>
/// Looking up a historical transaction and its receipt by hash, as <c>eth_getTransactionByHash</c> and
/// <c>eth_getTransactionReceipt</c> do, for blocks outside the block cache. The rotating lookups take each from a
/// different block, so none is served by the receipts cache; the same-block lookups are a backfill of one block.
/// </summary>
[MemoryDiagnoser]
public class TransactionLookupBenchmarks
{
    private const int Blocks = 200;

    private IContainer _container = null!;
    private IBlockchainBridge _bridge = null!;
    private Hash256[][] _hashes = null!;
    private int _next;

    [Params(50, 150)]
    public int Transactions { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        ISpecProvider specProvider = new TestSpecProvider(Prague.Instance);
        BlockTreeBuilder builder = Build.A.BlockTree().WithSpecProvider(specProvider);
        IBlockTree blockTree = builder.TestObject;
        IBlockStore blockStore = builder.BlockStore;
        TestMemColumnsDb<ReceiptsColumns> receiptsDb = new();
        PersistentReceiptStorage writer = CreateReceiptStorage(receiptsDb, specProvider, blockTree, blockStore);

        Block previous = Build.A.Block.Genesis.TestObject;
        BlockTreeBuilder.AddBlock(blockTree, previous);
        _hashes = new Hash256[Blocks][];
        ulong nonce = 0;
        for (int b = 0; b < Blocks; b++)
        {
            Transaction[] transactions = new Transaction[Transactions];
            TxReceipt[] receipts = new TxReceipt[Transactions];
            for (int i = 0; i < transactions.Length; i++)
            {
                int callDataLength = (i % 20) switch { < 10 => 0, < 14 => 68, < 17 => 260, < 19 => 1024, _ => 8192 };
                transactions[i] = Build.A.Transaction.WithType(TxType.EIP1559).WithChainId(specProvider.ChainId).WithNonce(nonce++)
                    .WithGasLimit(100_000).WithMaxFeePerGas(30 * Unit.GWei).WithMaxPriorityFeePerGas(Unit.GWei)
                    .WithTo(TestItem.Addresses[i % TestItem.Addresses.Length]).WithData(new byte[callDataLength])
                    .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
                receipts[i] = Build.A.Receipt.WithTransactionHash(transactions[i].Hash).WithSender(TestItem.AddressA)
                    .WithLogs(new LogEntry(TestItem.AddressB, [1, 2], [TestItem.KeccakA])).WithGasUsedTotal(21_000UL * (ulong)(i + 1)).TestObject;
            }

            previous = Build.A.Block.WithParent(previous).WithBaseFeePerGas(Unit.GWei).WithReceiptsRoot(TestItem.KeccakA)
                .WithTransactions(transactions).TestObject;
            BlockTreeBuilder.AddBlock(blockTree, previous);
            writer.Insert(previous, receipts);
            _hashes[b] = Array.ConvertAll(transactions, static tx => tx.Hash!);
        }

        ((IClearableCache)blockStore).ClearCache();
        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddSingleton(specProvider)
            .AddSingleton(blockTree)
            .AddSingleton(blockStore)
            .AddSingleton<IReceiptFinder>(CreateReceiptStorage(receiptsDb, specProvider, blockTree, blockStore))
            .Build();
        _bridge = _container.Resolve<IBlockchainBridge>();
    }

    [GlobalCleanup]
    public void Cleanup() => _container.Dispose();

    [Benchmark]
    public bool TryGetTransaction_rotating() => _bridge.TryGetTransaction(NextRotating(), out _, checkTxnPool: false);

    [Benchmark]
    public TxReceipt? GetTxReceiptInfo_rotating() => _bridge.GetTxReceiptInfo(NextRotating()).Receipt;

    [Benchmark]
    public bool TryGetTransaction_same_block() => _bridge.TryGetTransaction(_hashes[0][_next++ % Transactions], out _, checkTxnPool: false);

    private Hash256 NextRotating()
    {
        int n = _next++;
        return _hashes[n % Blocks][(n * 7) % Transactions];
    }

    private static PersistentReceiptStorage CreateReceiptStorage(TestMemColumnsDb<ReceiptsColumns> receiptsDb, ISpecProvider specProvider, IBlockTree blockTree, IBlockStore blockStore) =>
        new(receiptsDb, specProvider, new ReceiptsRecovery(new EthereumEcdsa(specProvider.ChainId), specProvider), blockTree, blockStore, new ReceiptConfig());
}

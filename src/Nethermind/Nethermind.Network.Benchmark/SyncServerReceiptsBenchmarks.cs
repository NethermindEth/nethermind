// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.History;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Test;
using NSubstitute;

namespace Nethermind.Network.Benchmarks;

/// <summary>
/// Serving GetReceipts for blocks older than the block tree's head cache, as a peer syncing receipts asks for them.
/// </summary>
[MemoryDiagnoser]
public class SyncServerReceiptsBenchmarks
{
    private const int ServedBlocks = 32;

    // Enough newer blocks to push the served ones out of the block tree's head cache.
    private const int NewerEmptyBlocks = 200;

    private SyncServer _server = null!;
    private Hash256[] _servedHashes = null!;

    [Params(50, 150, 300)]
    public int TransactionsPerBlock { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        TestSpecProvider specProvider = new(Byzantium.Instance);
        EthereumEcdsa ecdsa = new(specProvider.ChainId);
        BlockTreeBuilder builder = Build.A.BlockTree().WithSpecProvider(specProvider).OfChainLength(1);
        BlockTree blockTree = builder.TestObject;

        TestMemColumnsDb<ReceiptsColumns> receiptsDb = new();
        receiptsDb.GetColumnDb(ReceiptsColumns.Blocks).Set(Keccak.Zero, []);
        PersistentReceiptStorage receiptStorage = new(
            receiptsDb, specProvider, new ReceiptsRecovery(ecdsa, specProvider), blockTree, builder.BlockStore, new ReceiptConfig())
        { MigratedBlockNumber = 0 };

        _servedHashes = new Hash256[ServedBlocks];
        Block parent = blockTree.Head!;
        ulong nonce = 0;
        for (int number = 0; number < ServedBlocks + NewerEmptyBlocks; number++)
        {
            Transaction[] transactions = new Transaction[number < ServedBlocks ? TransactionsPerBlock : 0];
            for (int i = 0; i < transactions.Length; i++)
            {
                transactions[i] = Build.A.Transaction
                    .WithNonce(nonce++)
                    .WithGasLimit(GasCostOf.Transaction)
                    .Signed(ecdsa, TestItem.PrivateKeyA, true)
                    .TestObject;
            }

            Block block = Build.A.Block
                .WithParent(parent)
                .WithTransactions(transactions)
                .WithReceiptsRoot(transactions.Length == 0 ? Keccak.EmptyTreeHash : TestItem.KeccakA)
                .TestObject;
            blockTree.SuggestBlock(block);
            blockTree.TryUpdateMainChain(block.Header, true, false, block);

            if (transactions.Length != 0)
            {
                TxReceipt[] receipts = new TxReceipt[transactions.Length];
                ulong gasUsedTotal = 0;
                for (int i = 0; i < receipts.Length; i++)
                {
                    gasUsedTotal += GasCostOf.Transaction;
                    receipts[i] = new TxReceipt
                    {
                        TxHash = transactions[i].Hash,
                        BlockHash = block.Hash,
                        BlockNumber = block.Number,
                        Index = i,
                        GasUsedTotal = gasUsedTotal,
                        Sender = TestItem.AddressA,
                        StatusCode = StatusCode.Success,
                        Logs = [],
                        Bloom = Bloom.Empty
                    };
                }
                receiptStorage.Insert(block, receipts);
                _servedHashes[number] = block.Hash!;
            }

            parent = block;
        }

        // Serve from a fresh storage over the same database: receipts of old blocks are not in the receipts cache.
        PersistentReceiptStorage servingStorage = new(
            receiptsDb, specProvider, new ReceiptsRecovery(ecdsa, specProvider), blockTree, builder.BlockStore, new ReceiptConfig())
        { MigratedBlockNumber = 0 };

        _server = new SyncServer(
            Substitute.For<IWorldStateManager>(),
            new MemDb(),
            blockTree,
            new FullInfoReceiptFinder(servingStorage, new ReceiptsRecovery(ecdsa, specProvider), blockTree),
            Substitute.For<IBlockAccessListStore>(),
            Always.Valid,
            Always.Valid,
            Substitute.For<ISyncPeerPool>(),
            StaticSelector.Full,
            new TestSyncConfig(),
            Policy.FullGossip,
            Substitute.For<IHistoryPruner>(),
            specProvider,
            LimboLogs.Instance);

        foreach (Hash256 hash in _servedHashes)
        {
            if (_server.GetReceipts(hash)?.Length != TransactionsPerBlock)
                throw new InvalidOperationException($"Block {hash} does not serve its {TransactionsPerBlock} receipts.");
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _server.Dispose();

    [Benchmark(OperationsPerInvoke = ServedBlocks)]
    public int GetReceipts()
    {
        int served = 0;
        foreach (Hash256 hash in _servedHashes)
        {
            served += _server.GetReceipts(hash)!.Length;
        }
        return served;
    }
}

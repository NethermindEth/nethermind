// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Reflection;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Serialization.Rlp;
using NethermindRlp = Nethermind.Serialization.Rlp.Rlp;
using Nethermind.Specs;
using NSubstitute;

namespace Nethermind.Benchmarks.Store;

/// <summary>
/// The two ways a trace by hash finds its target in a block read from the database: hashing the transactions before it,
/// or reading the transaction hashes the stored receipts carry and hashing only the target.
/// </summary>
[MemoryDiagnoser]
public class TraceTransactionIndexBenchmarks
{
    private Block _block = null!;
    private byte[][] _encoded = null!;
    private Hash256 _target = null!;
    private PersistentReceiptStorage _receipts = null!;
    private Func<Transaction[], Hash256, int> _indexOf = null!;
    private readonly IPrefixStateSeedSource _seeds = new CoveringSeeds();

    [Params(16, 200)]
    public int Transactions { get; set; }

    [Params(0, 50, 100)]
    public int TargetPercent { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        Transaction[] transactions = new Transaction[Transactions];
        TxReceipt[] receipts = new TxReceipt[Transactions];
        _encoded = new byte[Transactions][];
        for (int i = 0; i < Transactions; i++)
        {
            transactions[i] = Build.A.Transaction.WithNonce((ulong)i).WithData(new byte[100]).SignedAndResolved().TestObject;
            _encoded[i] = NethermindRlp.Encode(transactions[i], RlpBehaviors.SkipTypedWrapping);
            receipts[i] = Build.A.Receipt.WithTransactionHash(transactions[i].Hash).WithGasUsedTotal((ulong)(21_000 * (i + 1))).TestObject;
        }

        _block = Build.A.Block.WithNumber(1).WithReceiptsRoot(TestItem.KeccakA).WithTransactions(transactions).TestObject;
        _target = transactions[Math.Min(Transactions - 1, Transactions * TargetPercent / 100)].Hash!;

        TestMemColumnsDb<ReceiptsColumns> receiptsDb = new();
        CreateStorage(receiptsDb).Insert(_block, receipts, ensureCanonical: false);
        // A new storage over the same database starts with an empty cache, as a trace of an older block does.
        _receipts = CreateStorage(receiptsDb);

        _indexOf = typeof(TransactionTraceBoundary).GetMethod("IndexOf", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate<Func<Transaction[], Hash256, int>>();

        if (HashPrefix() != StoredReceipts()) throw new InvalidOperationException("The two lookups disagree.");
    }

    [Benchmark(Baseline = true)]
    public int HashPrefix()
    {
        ForgetHashes();
        return _indexOf(_block.Transactions, _target);
    }

    [Benchmark]
    public int StoredReceipts()
    {
        ForgetHashes();
        return TransactionTraceBoundary.FindIndex(_receipts, _seeds, _block, _target);
    }

    [Benchmark]
    public void ForgetHashesOnly() => ForgetHashes();

    /// <summary>Puts the transactions back in the state of a block decoded from the database: encoded, not hashed.</summary>
    private void ForgetHashes()
    {
        Transaction[] transactions = _block.Transactions;
        for (int i = 0; i < transactions.Length; i++)
        {
            transactions[i].Hash = null;
            transactions[i].SetPreHash(_encoded[i]);
        }
    }

    private static PersistentReceiptStorage CreateStorage(TestMemColumnsDb<ReceiptsColumns> receiptsDb) =>
        new(receiptsDb,
            MainnetSpecProvider.Instance,
            new ReceiptsRecovery(new EthereumEcdsa(BlockchainIds.Mainnet), MainnetSpecProvider.Instance),
            Substitute.For<IBlockTree>(),
            Substitute.For<IBlockStore>(),
            new ReceiptConfig());

    private sealed class CoveringSeeds : IPrefixStateSeedSource
    {
        public bool Enabled => true;

        public bool TrySeed(Block block, int transactionIndex, StateReadOverlaySlot slot) => false;
    }
}

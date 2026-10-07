// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using NUnit.Framework;
using static Nethermind.Core.Test.Builders.FrameTxTestFrames;

namespace Nethermind.Facade.Test.Eth;

/// <summary>
/// A full block writes every transaction through one reused RPC object per type, so a value left over from the previous
/// transaction would leak into the next one's JSON; the view must also stop allocating an object per transaction.
/// </summary>
public class BlockTransactionsViewTests
{
    private static readonly TransactionForRpcContext Pending = new(BlockchainIds.Mainnet);
    private static readonly TransactionForRpcContext Mined = new(BlockchainIds.Mainnet, TestItem.KeccakB, 25_000_000, 7, 1_700_000_000, 10);

    [Test]
    public void Registered_transaction_types_have_distinct_reuse_slots()
    {
        int[] slots = DiverseTransactions().Select(static entry => entry.Tx.Type).Distinct()
            .Select(TransactionForRpc.TransactionJsonConverter.GetRepopulatableSlot).ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(slots, Is.Unique);
            Assert.That(slots, Is.All.InRange(0, TransactionForRpc.TransactionJsonConverter.RepopulatableSlotCount - 1));
        }
    }

    [Test]
    public void Derived_transaction_type_does_not_inherit_reuse()
    {
        TransactionForRpc.RegisterTransactionType<CustomTransactionForRpc>();

        Assert.That(TransactionForRpc.TransactionJsonConverter.GetRepopulatableSlot(CustomTransactionForRpc.TxType), Is.EqualTo(-1));
    }

    private sealed class CustomTransactionForRpc : LegacyTransactionForRpc, IFromTransaction<CustomTransactionForRpc>
    {
        public new static TxType TxType => (TxType)0x7e;

        public new static CustomTransactionForRpc FromTransaction(Transaction tx, in TransactionForRpcContext extraData) => new();
    }

    [Test]
    public void Refilling_an_instance_writes_what_a_fresh_one_writes()
    {
        (string Name, Transaction Tx)[] transactions = DiverseTransactions();
        TransactionForRpcContext[] contexts = [Pending, Mined];
        Dictionary<Type, int> crossShape = [];

        using (Assert.EnterMultipleScope())
        {
            foreach ((string firstName, Transaction first) in transactions)
                foreach ((string secondName, Transaction second) in transactions)
                    foreach (TransactionForRpcContext firstContext in contexts)
                        foreach (TransactionForRpcContext secondContext in contexts)
                        {
                            TransactionForRpc reused = TransactionForRpc.FromTransaction(first, firstContext);
                            if (reused.GetType() != TransactionForRpc.FromTransaction(second, secondContext).GetType()) continue;

                            reused.Populate(second, secondContext);
                            Assert.That(Serialize(reused), Is.EqualTo(Serialize(TransactionForRpc.FromTransaction(second, secondContext))), $"{firstName} then {secondName}");
                            if (firstName != secondName) crossShape[reused.GetType()] = crossShape.GetValueOrDefault(reused.GetType()) + 1;
                        }
        }

        // A reset skipped for some shapes only shows when an instance is refilled from a shape that sets the property.
        Assert.That(crossShape.Keys, Is.EquivalentTo(new[]
        {
            typeof(LegacyTransactionForRpc), typeof(AccessListTransactionForRpc), typeof(EIP1559TransactionForRpc),
            typeof(BlobTransactionForRpc), typeof(SetCodeTransactionForRpc), typeof(FrameTransactionForRpc),
        }), "every RPC type needs at least two shapes");
    }

    [Test]
    public void Block_view_writes_what_fresh_transactions_write()
    {
        (string Name, Transaction Tx)[] transactions = DiverseTransactions();

        using (Assert.EnterMultipleScope())
        {
            foreach ((string firstName, Transaction first) in transactions)
                foreach ((string secondName, Transaction second) in transactions)
                {
                    Block block = Build.A.Block.WithNumber(25_000_000).WithBaseFeePerGas(7).WithTransactions(first, second).TestObject;
                    BlockForRpc view = new(block, includeFullTransactionData: true, MainnetSpecProvider.Instance);
                    BlockForRpc materialized = new(block, includeFullTransactionData: true, MainnetSpecProvider.Instance) { Transactions = view.Transactions!.Full };

                    Assert.That(Serialize(view), Is.EqualTo(Serialize(materialized)), $"{firstName} then {secondName}");
                }
        }
    }

    [Test]
    public void Full_builds_new_objects_on_each_access()
    {
        Block block = Build.A.Block.WithTransactions(DiverseTransactions().Select(static t => t.Tx).Take(3).ToArray()).TestObject;
        BlockTransactions transactions = new BlockForRpc(block, includeFullTransactionData: true, MainnetSpecProvider.Instance).Transactions!;

        TransactionForRpc[] first = transactions.Full!;
        first[0].Hash = TestItem.KeccakH;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(transactions.Full, Is.Not.SameAs(first));
            Assert.That(transactions.Full![0].Hash, Is.EqualTo(block.Transactions[0].Hash), "a change to a returned object must not reach the next access");
        }
    }

    [Test]
    public void Unknown_transaction_type_fails_while_the_block_is_built()
    {
        Transaction unknown = Build.A.Transaction.WithType((TxType)0x55).TestObject;
        // Built directly with a known size, so no RLP encoding, which needs a decoder for the type, runs first.
        Block block = new(Build.A.BlockHeader.TestObject, [unknown], []) { EncodedSize = 100 };

        Assert.That(() => new BlockForRpc(block, includeFullTransactionData: true, MainnetSpecProvider.Instance), Throws.ArgumentException);
    }

    [Test]
    public void Writing_a_full_block_does_not_allocate_an_object_per_transaction()
    {
        const int count = 200;
        Transaction[] transactions = new Transaction[count];
        for (int i = 0; i < count; i++)
        {
            transactions[i] = Build.A.Transaction.WithType(TxType.EIP1559).WithChainId(BlockchainIds.Mainnet).WithNonce((ulong)i)
                .WithMaxFeePerGas(30).WithTo(TestItem.AddressB).SignedAndResolved().TestObject;
        }

        Block block = Build.A.Block.WithNumber(1).WithBaseFeePerGas(7).WithTransactions(transactions).TestObject;
        BlockForRpc view = new(block, includeFullTransactionData: true, MainnetSpecProvider.Instance);
        BlockForRpc materialized = new(block, includeFullTransactionData: true, MainnetSpecProvider.Instance);
        ArrayBufferWriter<byte> buffer = new(1 << 20);

        long fromView = MinAllocated(() => Write(view, buffer));
        // Full builds an object per transaction, which is what writing used to require.
        long fromObjects = MinAllocated(() =>
        {
            materialized.Transactions = view.Transactions!.Full;
            Write(materialized, buffer);
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fromView, Is.LessThan(count * 64), "transactions must be written from the stored ones, not materialized per transaction");
            Assert.That(fromView, Is.LessThan(fromObjects / 4));
        }
    }

    private static long MinAllocated(Action write)
    {
        long allocated = long.MaxValue;
        for (int i = 0; i < 5; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            write();
            allocated = Math.Min(allocated, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        return allocated;
    }

    private static void Write(BlockForRpc block, ArrayBufferWriter<byte> buffer)
    {
        buffer.ResetWrittenCount();
        using Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { SkipValidation = true, Encoder = EthereumJsonSerializer.JsonOptions.Encoder });
        TypeInfoJsonSerializer.Serialize(writer, block, EthereumJsonSerializer.JsonOptions);
    }

    private static string Serialize(object value) =>
        System.Text.Encoding.UTF8.GetString(TypeInfoJsonSerializer.SerializeToUtf8Bytes(value, value is TransactionForRpc ? typeof(TransactionForRpc) : value.GetType(), EthereumJsonSerializer.JsonOptions));

    /// <summary>Every transaction type with an RPC type, each in shapes that leave different properties set or empty.</summary>
    private static (string Name, Transaction Tx)[] DiverseTransactions()
    {
        AccessList accessList = new AccessList.Builder().AddAddress(TestItem.AddressC).AddStorage(UInt256.One).AddStorage(2).Build();
        Transaction unsigned = Build.A.Transaction.WithType(TxType.Legacy).WithChainId(null).WithTo(TestItem.AddressB).TestObject;
        unsigned.Signature = null;
        unsigned.Hash = TestItem.KeccakF;

        Transaction keyedFrame = FrameTx(SelfVerify(PrefixFrameGas), OnlyVerify());
        keyedFrame.NonceKeys = [1, 2];

        Transaction signedFrame = FrameTx(SelfVerify(PrefixFrameGas));
        signedFrame.FrameSignatures = [new TxFrameSignature(1, TestItem.AddressC, new byte[] { 1 }, new byte[] { 2, 3 })];
        signedFrame.RecentRootReferences = [new RecentRootReference(TestItem.KeccakC.ValueHash256, 5, TestItem.KeccakD.ValueHash256)];
        signedFrame.MaxFeePerBlobGas = 3;
        signedFrame.BlobVersionedHashes = [TestItem.KeccakE.BytesToArray()];

        return
        [
            ("legacy", Build.A.Transaction.WithType(TxType.Legacy).WithNonce(1).WithGasPrice(7).WithTo(TestItem.AddressB).WithValue(5).WithData([1, 2]).SignedAndResolved().TestObject),
            ("legacy, pre-155", Build.A.Transaction.WithType(TxType.Legacy).WithChainId(null).SignedAndResolved(TestItem.PrivateKeyA, isEip155Enabled: false).TestObject),
            ("legacy, create", Build.A.Transaction.WithType(TxType.Legacy).WithTo(null).WithCode([0x60, 0x00]).SignedAndResolved().TestObject),
            ("legacy, unsigned", unsigned),
            ("access list", Build.A.Transaction.WithType(TxType.AccessList).WithChainId(BlockchainIds.Mainnet).WithAccessList(accessList).SignedAndResolved().TestObject),
            ("access list, empty", Build.A.Transaction.WithType(TxType.AccessList).WithChainId(5).WithAccessList(AccessList.Empty).SignedAndResolved().TestObject),
            ("1559", Build.A.Transaction.WithType(TxType.EIP1559).WithChainId(BlockchainIds.Mainnet).WithMaxFeePerGas(30).WithMaxPriorityFeePerGas(2).WithAccessList(accessList).SignedAndResolved().TestObject),
            ("1559, create", Build.A.Transaction.WithType(TxType.EIP1559).WithChainId(BlockchainIds.Mainnet).WithTo(null).WithCode([0x60]).WithMaxFeePerGas(9).SignedAndResolved().TestObject),
            ("blob, with network wrapper", Build.A.Transaction.WithType(TxType.Blob).WithChainId(BlockchainIds.Mainnet).WithShardBlobTxTypeAndFields(2, isMempoolTx: true).WithMaxFeePerGas(30).SignedAndResolved().TestObject),
            ("blob, without network wrapper", Build.A.Transaction.WithType(TxType.Blob).WithChainId(BlockchainIds.Mainnet).WithShardBlobTxTypeAndFields(1, isMempoolTx: false).WithMaxFeePerGas(40).SignedAndResolved().TestObject),
            ("blob, higher blob fee cap", Build.A.Transaction.WithType(TxType.Blob).WithChainId(BlockchainIds.Mainnet).WithShardBlobTxTypeAndFields(1, isMempoolTx: false)
                .WithMaxFeePerBlobGas(9).WithMaxFeePerGas(40).SignedAndResolved().TestObject),
            ("set code", Build.A.Transaction.WithType(TxType.SetCode).WithChainId(BlockchainIds.Mainnet).WithAuthorizationCodeIfAuthorizationListTx().WithMaxFeePerGas(30).SignedAndResolved().TestObject),
            ("set code, two authorizations", Build.A.Transaction.WithType(TxType.SetCode).WithChainId(BlockchainIds.Mainnet)
                .WithAuthorizationCode([new AuthorizationTuple(1, TestItem.AddressC, 2, new Signature(new byte[64], 0)), new AuthorizationTuple(1, TestItem.AddressD, 3, new Signature(new byte[64], 1))])
                .WithMaxFeePerGas(30).SignedAndResolved().TestObject),
            ("frame", FrameTx(SelfVerify(PrefixFrameGas))),
            ("frame, nonce keys", keyedFrame),
            ("frame, signatures and root references", signedFrame),
        ];
    }
}

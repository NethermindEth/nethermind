// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V62;

[TestFixture, Parallelizable(ParallelScope.All)]
public class TransactionsMessageSerializerTests
{
    public enum BlobReturnMode { Retained, Returned, MalformedList, MalformedWrapper }

    [Test, NonParallelizable]
    public void Blob_buffers_are_reused_only_after_returning_the_transaction(
        [Values(131071, 131072, 131073)] int blobSize, [Values] BlobReturnMode returnMode)
    {
        TransactionsMessageSerializer serializer = new();
        byte[] blob = new byte[blobSize];
        Array.Fill(blob, (byte)0x11);
        Transaction source = Build.A.Transaction.WithType(TxType.Blob)
            .WithMaxFeePerBlobGas(1).WithBlobVersionedHashes(1).TestObject;
        source.Signature = new Signature(1, 2, 27);
        source.NetworkWrapper = new ShardBlobNetworkWrapper([blob], [], [], ProofVersion.V0);
        using TransactionsMessage message = new(new ArrayPoolList<Transaction>(1) { source });
        using DisposableByteBuffer buffer = Unpooled.Buffer().AsDisposable();
        serializer.Serialize(buffer, message);
        using TransactionsMessage first = serializer.Deserialize(buffer);
        Transaction tx = first.Transactions[0];
        byte[] original = ((ShardBlobNetworkWrapper)tx.NetworkWrapper).Blobs[0];
        first.Dispose();
        if (returnMode != BlobReturnMode.Retained) TxDecoder.TxObjectPool.Return(tx);
        if (returnMode == BlobReturnMode.MalformedList)
        {
            buffer.Clear();
            buffer.WriteBytes(Rlp.Encode(TxDecoder.Instance.Encode(source, RlpBehaviors.InMempoolForm), Rlp.OfEmptyList).Bytes);
            Assert.That(() => serializer.Deserialize(buffer), Throws.TypeOf<RlpException>());
        }
        if (returnMode == BlobReturnMode.MalformedWrapper)
        {
            buffer.Clear();
            serializer.Serialize(buffer, message);
            // Replace the empty commitments list with a byte string, after the blob was decoded.
            buffer.SetByte(buffer.WriterIndex - 2, 0x80);
            Assert.That(() => serializer.Deserialize(buffer), Throws.TypeOf<RlpException>());
        }

        Array.Fill(blob, (byte)0x22);
        buffer.Clear();
        serializer.Serialize(buffer, message);
        using TransactionsMessage second = serializer.Deserialize(buffer);
        byte[] next = ((ShardBlobNetworkWrapper)second.Transactions[0].NetworkWrapper).Blobs[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(next.Length, Is.EqualTo(blobSize));
            Assert.That(next, Is.All.EqualTo(0x22));
            Assert.That(ReferenceEquals(next, original), Is.EqualTo(returnMode != BlobReturnMode.Retained && blobSize == 131072));
            if (returnMode == BlobReturnMode.Retained) Assert.That(original, Is.All.EqualTo(0x11));
        }
        if (returnMode == BlobReturnMode.Retained) TxDecoder.TxObjectPool.Return(tx);
        TxDecoder.TxObjectPool.Return(second.Transactions[0]);
    }

    [Test]
    public void Roundtrip_init()
    {
        TransactionsMessageSerializer serializer = new();
        Transaction transaction = new()
        {
            GasLimit = 10,
            GasPrice = 100,
            Data = new byte[] { 4, 5, 6 },
            Nonce = 1000,
            Signature = new Signature(1, 2, 27),
            To = null,
            Value = 10000
        };
        transaction.Hash = transaction.CalculateHash();
        transaction.SenderAddress = null;

        using TransactionsMessage message = new(new ArrayPoolList<Transaction>(2) { transaction, transaction });
        SerializerTester.TestZero(serializer, message, "e2d08203e8640a80822710830405061b0102d08203e8640a80822710830405061b0102");
    }

    [Test]
    public void Roundtrip_call()
    {
        TransactionsMessageSerializer serializer = new();
        Transaction transaction = new()
        {
            Data = new byte[] { 1, 2, 3 },
            GasLimit = 10,
            GasPrice = 100,
            Nonce = 1000,
            Signature = new Signature(1, 2, 27),
            To = TestItem.AddressA,
            Value = 10000
        };
        transaction.Hash = transaction.CalculateHash();
        transaction.SenderAddress = null;

        using TransactionsMessage message = new(new ArrayPoolList<Transaction>(2) { transaction, transaction });
        SerializerTester.TestZero(serializer, message, "f84ae48203e8640a94b7705ae4c6f81b66cdb323c65f4e8133690fc099822710830102031b0102e48203e8640a94b7705ae4c6f81b66cdb323c65f4e8133690fc099822710830102031b0102");
    }

    [Test]
    public void Can_handle_empty()
    {
        TransactionsMessageSerializer serializer = new();
        using TransactionsMessage message = new(ArrayPoolList<Transaction>.Empty());

        SerializerTester.TestZero(serializer, message, EthSerializerGoldens.EmptyListRlp);
    }

    [Test]
    public void To_string_empty()
    {
        using TransactionsMessage message = new(ArrayPoolList<Transaction>.Empty());
        using TransactionsMessage message2 = new(null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(message.ToString(), Does.StartWith(nameof(TransactionsMessage)));
            Assert.That(message2.ToString(), Does.StartWith(nameof(TransactionsMessage)));
        }
    }

    [TestCaseSource(nameof(GetTransactionMessages))]
    public void Should_pass_roundtrip(TransactionsMessage transactionsMessage)
    {
        SerializerTester.TestZero(
            new TransactionsMessageSerializer(),
            transactionsMessage);
        transactionsMessage.Dispose();
    }

    [TestCaseSource(nameof(GetTransactionMessages))]
    public void Should_contain_network_form_tx_wrapper(TransactionsMessage transactionsMessage)
    {
        using DisposableByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer(1024 * 130).AsDisposable();
        TransactionsMessageSerializer serializer = new();
        serializer.Serialize(buffer, transactionsMessage);
        transactionsMessage.Dispose();
        using TransactionsMessage deserializedMessage = serializer.Deserialize(buffer);
        foreach (Transaction? tx in deserializedMessage.Transactions.Where(static tx => tx.SupportsBlobs))
        {
            Assert.That(tx.NetworkWrapper, Is.Not.Null);
        }

        foreach (Transaction? tx in deserializedMessage.Transactions.Where(static tx => !tx.SupportsBlobs))
        {
            Assert.That(tx.NetworkWrapper, Is.Null);
        }
    }

    [Test, NonParallelizable]
    public void Malformed_list_returns_previously_decoded_transaction()
    {
        TransactionsMessageSerializer serializer = new();
        // A valid signed legacy transaction followed by a null transaction.
        using DisposableByteBuffer buffer = Unpooled.WrappedBuffer(Bytes.FromHexString(
            "d2d08203e8640a80822710830405061b0102c0")).AsDisposable();
        Transaction reusable = TxDecoder.TxObjectPool.Get();
        TxDecoder.TxObjectPool.Return(reusable);

        Assert.That(() => serializer.Deserialize(buffer), Throws.TypeOf<RlpException>());

        Transaction returned = TxDecoder.TxObjectPool.Get();
        try
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(returned, Is.SameAs(reusable));
                Assert.That(returned.Signature, Is.Null);
                Assert.That(returned.Data.IsEmpty, Is.True);
                Assert.That(returned.Hash, Is.Null);
            }
        }
        finally
        {
            TxDecoder.TxObjectPool.Return(returned);
        }
    }

    private static IEnumerable<TransactionsMessage> GetTransactionMessages() =>
        GetTransactions().Select(static txs => new TransactionsMessage(txs.ToPooledList(3)));

    public static IEnumerable<IEnumerable<Transaction>> GetTransactions()
    {
        // simple transaction
        yield return new List<Transaction>
        {
            Build.A.Transaction
                .WithTo(TestItem.AddressA)
                .SignedAndResolved(new EthereumEcdsa(BlockchainIds.Sepolia), TestItem.PrivateKeyA)
                .TestObject
        };

        yield return new List<Transaction>
        {
            Build.A.Transaction
                .WithType(TxType.Legacy)
                .WithTo(TestItem.AddressA)
                .SignedAndResolved(new EthereumEcdsa(BlockchainIds.Sepolia), TestItem.PrivateKeyA)
                .TestObject,
            Build.A.Transaction
                .WithTo(TestItem.AddressA)
                .WithShardBlobTxTypeAndFields(1)
                .SignedAndResolved(new EthereumEcdsa(BlockchainIds.Sepolia), TestItem.PrivateKeyA)
                .TestObject,
            Build.A.Transaction
                .WithType(TxType.AccessList)
                .WithTo(TestItem.AddressA)
                .SignedAndResolved(new EthereumEcdsa(BlockchainIds.Sepolia), TestItem.PrivateKeyA)
                .TestObject,
            Build.A.Transaction
                .WithTo(TestItem.AddressA)
                .WithShardBlobTxTypeAndFields(2)
                .SignedAndResolved(new EthereumEcdsa(BlockchainIds.Sepolia), TestItem.PrivateKeyA)
                .TestObject,
            Build.A.Transaction
                .WithType(TxType.EIP1559)
                .WithTo(TestItem.AddressA)
                .SignedAndResolved(new EthereumEcdsa(BlockchainIds.Sepolia), TestItem.PrivateKeyA)
                .TestObject,
            Build.A.Transaction
                .WithTo(TestItem.AddressA)
                .WithShardBlobTxTypeAndFields(3)
                .SignedAndResolved(new EthereumEcdsa(BlockchainIds.Sepolia), TestItem.PrivateKeyA)
                .TestObject,
            Build.A.Transaction
                .WithType(TxType.EIP1559)
                .SignedAndResolved(new EthereumEcdsa(BlockchainIds.Sepolia), TestItem.PrivateKeyA)
                .TestObject,
        };

        // several shard blob transactions
        yield return new List<Transaction>
        {
            Build.A.Transaction
                .WithTo(TestItem.AddressA)
                .WithShardBlobTxTypeAndFields(1)
                .SignedAndResolved(new EthereumEcdsa(BlockchainIds.Sepolia), TestItem.PrivateKeyA)
                .TestObject,
            Build.A.Transaction
                .WithTo(TestItem.AddressA)
                .WithShardBlobTxTypeAndFields(2)
                .SignedAndResolved(new EthereumEcdsa(BlockchainIds.Sepolia), TestItem.PrivateKeyA)
                .TestObject
        };
    }
}

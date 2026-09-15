// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V62;

[TestFixture, Parallelizable(ParallelScope.All)]
public class TransactionsMessageSerializerTests
{
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

    [Test]
    public void Skips_an_oversized_item_instead_of_decoding_it()
    {
        const int maxTxSize = 500;
        Transaction validTx = SimpleSignedTx();
        byte[] validTxBytes = TxDecoder.Instance.Encode(validTx, RlpBehaviors.InMempoolForm).Bytes;
        byte[] oversizedItem = EncodeOversizedTypedItem(maxTxSize + 1, (byte)TxType.EIP1559);
        using DisposableByteBuffer buffer = Unpooled.WrappedBuffer(EncodeAsSequence(validTxBytes, oversizedItem)).AsDisposable();

        TransactionsMessageSerializer serializer = new(new TxPoolConfig { MaxTxSize = maxTxSize });
        using TransactionsMessage deserialized = serializer.Deserialize(buffer);

        Assert.That(deserialized.Transactions.Count, Is.EqualTo(1), "only the well-formed tx should survive");
        Assert.That(deserialized.Transactions[0].Hash, Is.EqualTo(validTx.Hash), "cursor should have resynchronised on the valid tx");
    }

    [Test]
    public void Keeps_a_typed_tx_whose_size_is_exactly_the_configured_max()
    {
        Transaction typedTx = Build.A.Transaction
            .WithType(TxType.EIP1559)
            .WithTo(TestItem.AddressA)
            .SignedAndResolved(new EthereumEcdsa(BlockchainIds.Sepolia), TestItem.PrivateKeyA)
            .TestObject;
        RlpReader probe = new(TxDecoder.Instance.Encode(typedTx, RlpBehaviors.InMempoolForm).Bytes);
        (int _, int contentLength) = probe.PeekPrefixAndContentLength();

        TransactionsMessageSerializer serializer = new(new TxPoolConfig { MaxTxSize = contentLength });
        using TransactionsMessage message = new(new[] { typedTx }.ToPooledList());
        using DisposableByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer(1024).AsDisposable();
        serializer.Serialize(buffer, message);
        using TransactionsMessage deserialized = serializer.Deserialize(buffer);

        Assert.That(deserialized.Transactions.Count, Is.EqualTo(1), "a tx exactly at the cap must be kept, not skipped");
    }

    [Test]
    public void Keeps_a_blob_tx_above_max_tx_size_but_within_the_blob_cap()
    {
        Transaction blobTx = Build.A.Transaction
            .WithTo(TestItem.AddressA)
            .WithShardBlobTxTypeAndFields(1)
            .SignedAndResolved(new EthereumEcdsa(BlockchainIds.Sepolia), TestItem.PrivateKeyA)
            .TestObject;
        // The blob tx's mempool-form size (~128 KiB of sidecar) is far above this tiny cap, so it is only
        // kept if the blob-specific cap - driven by the type byte, not the length - is applied instead.
        TxPoolConfig config = new() { MaxTxSize = 500, MaxBlobTxSize = 500 };
        ISpecProvider specProvider = new TestSpecProvider(Cancun.Instance);
        TransactionsMessageSerializer serializer = new(config, specProvider);

        using TransactionsMessage message = new(new[] { blobTx }.ToPooledList());
        using DisposableByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer(1024 * 130).AsDisposable();
        serializer.Serialize(buffer, message);
        using TransactionsMessage deserialized = serializer.Deserialize(buffer);

        Assert.That(deserialized.Transactions.Count, Is.EqualTo(1));
    }

    [Test]
    public void Null_config_keeps_every_transaction_regardless_of_size()
    {
        List<Transaction> transactions = GetTransactions().ElementAt(1).ToList();
        using TransactionsMessage message = new(transactions.ToPooledList());
        TransactionsMessageSerializer serializer = new();

        using DisposableByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer(1024 * 130).AsDisposable();
        serializer.Serialize(buffer, message);
        using TransactionsMessage deserialized = serializer.Deserialize(buffer);

        Assert.That(deserialized.Transactions.Count, Is.EqualTo(transactions.Count));
    }

    internal static Transaction SimpleSignedTx() =>
        Build.A.Transaction
            .WithTo(TestItem.AddressA)
            .SignedAndResolved(new EthereumEcdsa(BlockchainIds.Sepolia), TestItem.PrivateKeyA)
            .TestObject;

    /// <summary>
    /// Builds a well-formed RLP long-form byte string (prefix 0xb9, a 2-byte length) declaring
    /// <paramref name="contentLength"/> content bytes: <paramref name="typeByte"/> followed by 0xff, which is
    /// not a valid start of the nested RLP structure a real decode would expect next - so attempting to decode
    /// this item throws, distinguishing a test that skipped it from one that decoded it and happened to succeed.
    /// </summary>
    internal static byte[] EncodeOversizedTypedItem(int contentLength, byte typeByte)
    {
        byte[] item = new byte[3 + contentLength];
        item[0] = 0xb9;
        item[1] = (byte)(contentLength >> 8);
        item[2] = (byte)contentLength;
        item[3] = typeByte;
        item[4] = 0xff;
        return item;
    }

    /// <summary>Concatenates already-encoded RLP items behind a single outer sequence (list) prefix.</summary>
    internal static byte[] EncodeAsSequence(params byte[][] items)
    {
        int contentLength = 0;
        foreach (byte[] item in items) contentLength += item.Length;

        byte[] result = new byte[Rlp.LengthOfSequence(contentLength)];
        int prefixLength = result.Length - contentLength;
        if (prefixLength == 1)
        {
            result[0] = (byte)(0xc0 + contentLength);
        }
        else
        {
            int lengthOfLength = prefixLength - 1;
            result[0] = (byte)(0xf7 + lengthOfLength);
            int remaining = contentLength;
            for (int i = lengthOfLength; i >= 1; i--)
            {
                result[i] = (byte)remaining;
                remaining >>= 8;
            }
        }

        int offset = prefixLength;
        foreach (byte[] item in items)
        {
            item.CopyTo(result, offset);
            offset += item.Length;
        }
        return result;
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

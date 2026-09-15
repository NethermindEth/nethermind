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

    [TestCaseSource(nameof(OversizedItemCases))]
    public void Skips_an_oversized_item_instead_of_decoding_it(int maxTxSize, byte[] oversizedItem)
    {
        Transaction validTxBefore = SimpleSignedTx();
        Transaction validTxAfter = SimpleSignedTx(1);
        byte[] validTxBeforeBytes = TxDecoder.Instance.Encode(validTxBefore, RlpBehaviors.InMempoolForm).Bytes;
        byte[] validTxAfterBytes = TxDecoder.Instance.Encode(validTxAfter, RlpBehaviors.InMempoolForm).Bytes;
        using DisposableByteBuffer buffer = Unpooled.WrappedBuffer(EncodeAsSequence(validTxBeforeBytes, oversizedItem, validTxAfterBytes)).AsDisposable();

        TransactionsMessageSerializer serializer = new(new TxPoolConfig { MaxTxSize = maxTxSize });
        using TransactionsMessage deserialized = serializer.Deserialize(buffer);

        Assert.That(deserialized.Transactions.Count, Is.EqualTo(2), "only the two well-formed txs should survive");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(deserialized.Transactions[0].Hash, Is.EqualTo(validTxBefore.Hash), "the tx before the skip should decode unaffected");
            Assert.That(deserialized.Transactions[1].Hash, Is.EqualTo(validTxAfter.Hash), "cursor should have resynchronised on the tx after the skip");
        }
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

    [TestCaseSource(nameof(BlobCapCases))]
    public void Measures_a_blob_tx_against_the_blob_cap(IReleaseSpec spec, long? maxTxSize, long maxBlobTxSize, int expectedCount)
    {
        Transaction blobTx = Build.A.Transaction
            .WithTo(TestItem.AddressA)
            .WithShardBlobTxTypeAndFields(1)
            .SignedAndResolved(new EthereumEcdsa(BlockchainIds.Sepolia), TestItem.PrivateKeyA)
            .TestObject;
        TxPoolConfig config = new() { MaxTxSize = maxTxSize, MaxBlobTxSize = maxBlobTxSize };
        ISpecProvider specProvider = new TestSpecProvider(spec);
        TransactionsMessageSerializer serializer = new(config, specProvider);

        using TransactionsMessage message = new(new[] { blobTx }.ToPooledList());
        using DisposableByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer(1024 * 130).AsDisposable();
        serializer.Serialize(buffer, message);
        using TransactionsMessage deserialized = serializer.Deserialize(buffer);

        Assert.That(deserialized.Transactions.Count, Is.EqualTo(expectedCount));
    }

    [TestCaseSource(nameof(MaxBlobCountCases))]
    public void Keeps_a_blob_tx_sitting_exactly_on_the_pool_size_boundary(IReleaseSpec spec)
    {
        Transaction blobTx = Build.A.Transaction
            .WithTo(TestItem.AddressA)
            .WithShardBlobTxTypeAndFields((int)spec.MaxBlobCount, spec: spec)
            .SignedAndResolved(new EthereumEcdsa(BlockchainIds.Sepolia), TestItem.PrivateKeyA)
            .TestObject;
        // SizeTxFilter accepts a blob tx whose consensus encoding is at most MaxBlobTxSize, so a tx of the
        // largest blob count sitting exactly on that boundary is the tightest case the guard must let through.
        TxPoolConfig config = new() { MaxBlobTxSize = blobTx.GetLength(shouldCountBlobs: false) };
        TransactionsMessageSerializer serializer = new(config, new TestSpecProvider(spec));

        using TransactionsMessage message = new(new[] { blobTx }.ToPooledList());
        using DisposableByteBuffer buffer = Unpooled.Buffer(serializer.GetLength(message, out _)).AsDisposable();
        serializer.Serialize(buffer, message);
        using TransactionsMessage deserialized = serializer.Deserialize(buffer);

        Assert.That(deserialized.Transactions.Count, Is.EqualTo(1), "a blob tx the pool would accept must never be skipped");
    }

    // Each prefix declares 55 content bytes the buffer does not carry: 0xb7 as a byte string, 0xf7 as a
    // sequence. A cap below the short-form maximum would let the byte string reach the type-byte peek and the
    // sequence reach the unchecked skip, both of which run past the end of the buffer - letting the sender pick
    // the exception an unguarded decode would have raised as an RLP error.
    [TestCase((byte)0xb7, TestName = "A truncated short-form byte string under a tiny cap still fails as an RLP error")]
    [TestCase((byte)0xf7, TestName = "A truncated short-form sequence under a tiny cap still fails as an RLP error")]
    public void A_size_limit_below_the_rlp_short_form_maximum_does_not_change_how_a_truncated_item_fails(byte itemPrefix)
    {
        using DisposableByteBuffer buffer = Unpooled.WrappedBuffer(EncodeAsSequence(new[] { itemPrefix })).AsDisposable();
        TransactionsMessageSerializer serializer = new(new TxPoolConfig { MaxTxSize = 10 });

        Assert.That(() => serializer.Deserialize(buffer).Dispose(), Throws.InstanceOf<RlpException>());
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

    internal static Transaction SimpleSignedTx(ulong nonce = 0) =>
        Build.A.Transaction
            .WithNonce(nonce)
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

    /// <summary>
    /// Builds a well-formed RLP long-form sequence (prefix 0xf9, a 2-byte length) declaring
    /// <paramref name="contentLength"/> content bytes starting with 0xff, which is not a valid start of a
    /// legacy transaction's field list - so attempting to decode this item throws, distinguishing a test that
    /// skipped it from one that decoded it and happened to succeed. Its total on-wire size - the measure the
    /// guard applies to a sequence item - is three bytes beyond <paramref name="contentLength"/>.
    /// </summary>
    private static byte[] EncodeOversizedSequenceItem(int contentLength)
    {
        byte[] item = new byte[3 + contentLength];
        item[0] = 0xf9;
        item[1] = (byte)(contentLength >> 8);
        item[2] = (byte)contentLength;
        item[3] = 0xff;
        return item;
    }

    private static IEnumerable<TestCaseData> OversizedItemCases()
    {
        const int maxTxSize = 500;

        yield return new TestCaseData(maxTxSize, EncodeOversizedTypedItem(maxTxSize + 1, (byte)TxType.EIP1559))
            .SetName("Skips a typed item whose content length exceeds the cap");
        // A sequence item is measured the way SizeTxFilter measures a decoded legacy tx, prefix included, so
        // content exactly at the cap is already over it.
        yield return new TestCaseData(maxTxSize, EncodeOversizedSequenceItem(maxTxSize))
            .SetName("Skips a sequence item its RLP prefix pushes over the cap");
    }

    private static IEnumerable<TestCaseData> BlobCapCases()
    {
        // A one-blob tx's mempool-form size (~128 KiB of sidecar) is far above either cap below, so whether
        // it survives turns on the blob cap alone - which Cancun's blob-gas allowance raises past it and
        // Shanghai's, carrying no blobs, does not.
        yield return new TestCaseData(Cancun.Instance, 500L, 500L, 1)
            .SetName("Keeps a blob tx above MaxTxSize but within the blob cap");
        yield return new TestCaseData(Shanghai.Instance, null, 500L, 0)
            .SetName("Skips a blob tx above the blob cap with no MaxTxSize configured");
        // MaxBlobTxSize is unvalidated, so an absurd value has to leave the blob cap effectively absent rather
        // than wrap the sidecar allowance past either end of the range and collapse the cap onto its floor.
        yield return new TestCaseData(Cancun.Instance, null, long.MaxValue, 1)
            .SetName("Keeps a blob tx when MaxBlobTxSize would overflow the sidecar allowance");
        yield return new TestCaseData(Cancun.Instance, null, long.MinValue, 1)
            .SetName("Keeps a blob tx when MaxBlobTxSize is negative");
    }

    private static IEnumerable<TestCaseData> MaxBlobCountCases()
    {
        yield return new TestCaseData(Cancun.Instance)
            .SetName("Keeps a six-blob tx at the boundary, one proof per blob");
        yield return new TestCaseData(Prague.Instance)
            .SetName("Keeps a nine-blob tx at the boundary, one proof per blob");
        yield return new TestCaseData(Osaka.Instance)
            .SetName("Keeps a nine-blob tx at the boundary, one EIP-7594 cell proof per cell");
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

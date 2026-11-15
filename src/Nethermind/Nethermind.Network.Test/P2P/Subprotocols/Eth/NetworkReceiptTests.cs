// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Encoding;
using Nethermind.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.State.Proofs;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth;

[Parallelizable(ParallelScope.All)]
public class NetworkReceiptTests
{
    private static readonly ReceiptMessageDecoder Decoder = new();

    [Test]
    public void Root_and_sizes_match_the_decoded_receipts([Values] bool withBloom, [Values] bool eip658, [Values] bool eip7668)
    {
        ReleaseSpec spec = new() { IsEip658Enabled = eip658, IsEip7668Enabled = eip7668 };
        TxReceipt[] receipts = CreateReceipts(eip658);
        byte[] message = Encode(receipts, withBloom, eip658);

        Assert.That(ReceiptTrie.TryCalculateRoot(spec, message, withBloom, out Hash256 root) ? root : null,
            Is.EqualTo(ReceiptTrie.CalculateRoot(spec, receipts, Decoder)));

        RlpReader reader = new RlpReader(message).EnterSequence(out _);
        RlpBehaviors behaviors = eip658 ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None;
        foreach (TxReceipt receipt in receipts)
        {
            Assert.That(NetworkReceipt.TryRead(ref reader, withBloom, out NetworkReceipt read), Is.True);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(read.GasUsedTotal, Is.EqualTo(receipt.GasUsedTotal));
                Assert.That(read.LogsGas, Is.EqualTo(receipt.Logs!.Aggregate(0UL, static (gas, log) =>
                    gas + GasCostOf.Log + (ulong)log.Topics.Length * GasCostOf.LogTopic + (ulong)log.Data.Length * GasCostOf.LogData)));
                Assert.That(read.GetMessageLength(eip658), Is.EqualTo(Decoder.GetLength(receipt, behaviors)));
            }
        }
    }

    [Test]
    public void Validated_reads_preserve_receipt_fields_and_advance_to_the_next_receipt([Values] bool withBloom, [Values] bool eip658)
    {
        byte[] message = Encode(CreateReceipts(eip658), withBloom, eip658);
        RlpReader validating = new RlpReader(message).EnterSequence(out _);
        RlpReader validated = validating;

        while (validating.HasNext)
        {
            Assert.That(NetworkReceipt.TryRead(ref validating, withBloom, out NetworkReceipt expected), Is.True);
            NetworkReceipt actual = NetworkReceipt.ReadValidated(ref validated, withBloom);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(actual.Type, Is.EqualTo(expected.Type));
                Assert.That(actual.StatusOrState.SequenceEqual(expected.StatusOrState), Is.True);
                Assert.That(actual.GasUsedTotalItem.SequenceEqual(expected.GasUsedTotalItem), Is.True);
                Assert.That(actual.GasUsedTotal, Is.EqualTo(expected.GasUsedTotal));
                Assert.That(actual.Bloom.SequenceEqual(expected.Bloom), Is.True);
                Assert.That(actual.Logs.SequenceEqual(expected.Logs), Is.True);
                Assert.That(actual.LogsGas, Is.Zero);
                Assert.That(validated.BytesRead, Is.EqualTo(validating.BytesRead));
            }
        }

        Assert.That(validated.HasNext, Is.False);
    }

    [Test]
    public void Validates_topic_count_while_reading_logs([Values] bool withBloom, [Values(0, 4, 5)] int topicCount)
    {
        LogEntry log = new(TestItem.AddressA, [1, 2], Enumerable.Repeat(TestItem.KeccakA, topicCount).ToArray());
        TxReceipt receipt = Build.A.Receipt.WithStatusCode(1).WithLogs(log).WithCalculatedBloom().TestObject;
        byte[] message = Encode([receipt], withBloom, eip658: true);
        RlpReader reader = new RlpReader(message).EnterSequence(out _);

        Assert.That(NetworkReceipt.TryRead(ref reader, withBloom, out _), Is.EqualTo(topicCount <= 4));
    }

    [TestCase(null, true, TestName = "Valid receipt")]
    [TestCase("extra field", false)]
    [TestCase("trailing payload bytes", false)]
    [TestCase("status byte", false)]
    [TestCase("address length", false)]
    [TestCase("topic length", false)]
    public void Reads_only_receipts_in_the_form_the_decoder_writes(string? malformation, bool expected)
    {
        byte[] log = RlpTestHelper.EncodeSequence(
            Rlp.Encode(new byte[malformation == "address length" ? Address.Size - 1 : Address.Size]),
            RlpTestHelper.EncodeSequence(Rlp.Encode(new byte[malformation == "topic length" ? Hash256.Size - 1 : Hash256.Size])),
            Rlp.Encode(new byte[] { 1, 2 }));
        byte[][] fields =
        [
            malformation == "status byte" ? [0x02] : [0x01],
            Rlp.Encode(21_000UL),
            Rlp.Encode(new byte[Bloom.ByteLength]),
            RlpTestHelper.EncodeSequence(log),
            .. malformation == "extra field" ? new[] { RlpTestHelper.OfEmptyByteArray } : [],
        ];
        byte[] payload = [(byte)TxType.EIP1559, .. RlpTestHelper.EncodeSequence(fields), .. malformation == "trailing payload bytes" ? new byte[] { 0x00 } : []];
        byte[] item = Rlp.Encode(payload);
        byte[] message = RlpTestHelper.EncodeSequence(item);
        RlpReader reader = new RlpReader(message).EnterSequence(out _);

        bool read;
        try
        {
            read = NetworkReceipt.TryRead(ref reader, withBloom: true, out _);
        }
        catch (RlpReaderException)
        {
            read = false;
        }

        Assert.That(read, Is.EqualTo(expected));
        if (expected) Assert.That(Decoder.Decode(item).Logs, Has.Length.EqualTo(1));
    }

    [Test]
    public void Root_takes_zero_length_blooms_only_under_eip7668([Values] bool eip7668)
    {
        ReleaseSpec spec = new() { IsEip658Enabled = true, IsEip7668Enabled = eip7668 };
        TxReceipt[] receipts = CreateReceipts(eip658: true);
        byte[] message = RlpTestHelper.EncodeSequence([.. receipts.Select(static receipt => Decoder.Encode(receipt, RlpBehaviors.Eip658Receipts | RlpBehaviors.Eip7668Receipts))]);

        Assert.That(ReceiptTrie.TryCalculateRoot(spec, message, withBloom: true, out Hash256 root) ? root : null,
            Is.EqualTo(eip7668 ? ReceiptTrie.CalculateRoot(spec, receipts, Decoder) : null));
    }

    [Test]
    public void Root_uses_the_bloom_of_the_logs_when_the_message_has_a_zero_one()
    {
        ReleaseSpec spec = new() { IsEip658Enabled = true };
        TxReceipt[] receipts = CreateReceipts(eip658: true);
        Hash256 expected = ReceiptTrie.CalculateRoot(spec, receipts, Decoder);
        foreach (TxReceipt receipt in receipts) receipt.Bloom = Bloom.Empty;

        Assert.That(ReceiptTrie.TryCalculateRoot(spec, Encode(receipts, withBloom: true, eip658: true), withBloom: true, out Hash256 root) ? root : null,
            Is.EqualTo(expected));
    }

    [Test]
    public void Keeps_zero_length_blooms_encoded_only_where_the_decoder_takes_them([Values] bool allowZeroLengthBloom)
    {
        TxReceipt[] receipts = CreateReceipts(eip658: true);
        byte[] block = RlpTestHelper.EncodeSequence([.. receipts.Select(static receipt => Decoder.Encode(receipt, RlpBehaviors.Eip658Receipts | RlpBehaviors.Eip7668Receipts))]);
        byte[] message = RlpTestHelper.EncodeSequence(block);

        using NetworkBlockReceipts? kept = NetworkBlockReceipts.TryCreate(message, withBloom: true, static (ref RlpReader _) => [],
            RlpLimit.For<TxReceipt[]>(16, "blocks"), RlpLimit.For<TxReceipt>(16, "receipts"), allowZeroLengthBloom);

        Assert.That(kept is not null, Is.EqualTo(allowZeroLengthBloom));
    }

    [Test]
    public void Leaves_deposit_receipts_to_the_decoder([Values] bool withBloom)
    {
        TxReceipt[] receipts = [Build.A.Receipt.WithTxType(TxType.DepositTx).WithStatusCode(1).WithCalculatedBloom().TestObject];
        byte[] message = RlpTestHelper.EncodeSequence(Encode(receipts, withBloom, eip658: true));

        using NetworkBlockReceipts? kept = NetworkBlockReceipts.TryCreate(message, withBloom, static (ref RlpReader _) => [],
            RlpLimit.For<TxReceipt[]>(16, "blocks"), RlpLimit.For<TxReceipt>(16, "receipts"), allowZeroLengthBloom: false);

        Assert.That(kept, Is.Null);
    }

    [Test]
    public void Stored_receipts_match_the_decoded_and_recovered_ones([Values] bool withBloom, [Values] bool eip658, [Values] bool storeTxHashes)
    {
        TxReceipt[] receipts = CreateReceipts(eip658);
        byte[] message = Encode(receipts, withBloom, eip658);
        Transaction[] transactions = new Transaction[receipts.Length];
        for (int i = 0; i < receipts.Length; i++)
        {
            transactions[i] = new Transaction { Type = receipts[i].TxType, Hash = TestItem.Keccaks[i] };
            receipts[i].TxHash = TestItem.Keccaks[i];
        }

        ReceiptArrayStorageDecoder storageDecoder = new(compactEncoding: true, storeTxHashes);
        RlpBehaviors behaviors = RlpBehaviors.Storage | (eip658 ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None);

        using ArrayPoolSpan<byte> stored = storageDecoder.EncodeNetworkReceipts(message, withBloom, eip658, transactions);

        Assert.That(stored.ToArray(), Is.EqualTo(storageDecoder.Encode(receipts, behaviors)));
    }

    [Test]
    public void Stored_receipts_encode_as_the_decoded_and_recovered_ones([Values] bool withBloom, [Values] bool eip658, [Values] bool eip7668)
    {
        TxReceipt[] receipts = CreateReceipts(eip658);
        // Recovery takes a failed receipt with logs as successful.
        receipts[1].Logs = [Build.A.LogEntry.WithAddress(TestItem.AddressD).WithTopics(TestItem.KeccakD).WithData([0x7f]).TestObject];
        receipts[3].Logs =
        [
            Build.A.LogEntry.WithAddress(TestItem.AddressE).WithData([0x80]).TestObject,
            Build.A.LogEntry.WithAddress(TestItem.AddressA).WithTopics().WithData([0]).TestObject,
        ];
        receipts[2].Logs = [Build.A.LogEntry.WithAddress(TestItem.AddressF).WithData([.. new byte[300], 1, 2]).TestObject];
        Block block = Build.A.Block.WithNumber(1)
            .WithTransactions([.. receipts.Select(static receipt => CreateTransaction(receipt.TxType))]).TestObject;
        byte[] encodedBlock = new BlockDecoder().Encode(block);
        RlpBehaviors storageBehaviors = RlpBehaviors.Storage | (eip658 ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None);
        byte[] stored = ReceiptArrayStorageDecoder.Instance.Encode(receipts, storageBehaviors);

        // The compact form can also store the data [0x00] as a zero prefix of one and no data, which the decoder reads.
        int zeroData = stored.AsSpan().IndexOf((byte[])[.. TestItem.AddressA.Bytes, Rlp.EmptyListByte, Rlp.EmptyByteArrayByte, 0]) + Address.Size + 1;
        Assert.That(zeroData, Is.GreaterThan(Address.Size + 1));
        stored[zeroData] = 1;
        stored[zeroData + 1] = Rlp.EmptyByteArrayByte;

        TxReceipt[] decoded = ReceiptArrayStorageDecoder.Instance.Decode(stored, eip7668 ? RlpBehaviors.Eip7668Receipts : RlpBehaviors.None);
        new ReceiptsRecovery(new EthereumEcdsa(BlockchainIds.Mainnet), MainnetSpecProvider.Instance).TryRecover(new ReceiptRecoveryBlock(block), decoded, forceRecoverSender: false);

        // The receipts are added after what the list already holds.
        using ArrayPoolList<byte> network = new(1) { 0xff };
        bool encoded = ReceiptArrayStorageDecoder.TryAppendNetworkReceipts(stored, encodedBlock, withBloom, eip658, eip7668, network);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(encoded, Is.True);
            Assert.That(network.AsSpan().ToArray(), Is.EqualTo((byte[])[0xff, .. Encode(decoded, withBloom, eip658)]));
        }
    }

    [Test]
    [TestCase(-1, TxType.Legacy, TestName = "Fewer transactions")]
    [TestCase(1, TxType.Legacy, TestName = "More transactions")]
    [TestCase(0, TxType.DepositTx, TestName = "Deposit transaction")]
    public void Leaves_stored_receipts_that_do_not_match_the_block_to_the_decoder(int extraTransactions, TxType firstType)
    {
        TxReceipt[] receipts = CreateReceipts(eip658: true);
        // Only the transaction types are read, so each transaction is its type alone.
        byte[][] transactions = [.. Enumerable.Range(0, receipts.Length + extraTransactions)
            .Select(i => i == 0 ? firstType : i < receipts.Length ? receipts[i].TxType : TxType.Legacy)
            .Select(static type => type == TxType.Legacy ? [Rlp.EmptyListByte] : Rlp.Encode(new byte[] { (byte)type, Rlp.EmptyListByte }))];
        byte[] encodedBlock = RlpTestHelper.EncodeSequence([Rlp.EmptyListByte], RlpTestHelper.EncodeSequence(transactions));
        byte[] stored = ReceiptArrayStorageDecoder.Instance.Encode(receipts, RlpBehaviors.Storage | RlpBehaviors.Eip658Receipts);

        using ArrayPoolList<byte> destination = new(0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ReceiptArrayStorageDecoder.TryAppendNetworkReceipts(stored, encodedBlock, withBloom: false, eip658: true, eip7668: false, destination), Is.False);
            Assert.That(destination, Is.Empty);
        }
    }

    private static Transaction CreateTransaction(TxType type) => type switch
    {
        TxType.Blob => Build.A.Transaction.WithShardBlobTxTypeAndFields(isMempoolTx: false).TestObject,
        TxType.SetCode => Build.A.Transaction.WithType(TxType.SetCode).WithAuthorizationCodeIfAuthorizationListTx().TestObject,
        _ => Build.A.Transaction.WithType(type).TestObject,
    };

    private static TxReceipt[] CreateReceipts(bool eip658)
    {
        LogEntry[] logs =
        [
            Build.A.LogEntry.WithAddress(TestItem.AddressA).WithTopics(TestItem.KeccakA, TestItem.KeccakB).WithData([0, 0, 1, 2]).TestObject,
            Build.A.LogEntry.WithAddress(TestItem.AddressB).WithTopics().WithData([]).TestObject,
            Build.A.LogEntry.WithAddress(TestItem.AddressC).WithTopics(Keccak.Zero, new Hash256("0x0000000000000000000000000000000000000000000000000000000000000001"))
                .WithData([0, 0, 0]).TestObject,
        ];
        TxReceipt[] receipts =
        [
            Build.A.Receipt.WithTxType(TxType.Legacy).WithStatusCode(1).WithGasUsedTotal(21_000).WithLogs(logs).TestObject,
            Build.A.Receipt.WithTxType(TxType.EIP1559).WithStatusCode(0).WithGasUsedTotal(50_000).WithLogs().TestObject,
            Build.A.Receipt.WithTxType(TxType.AccessList).WithStatusCode(1).WithGasUsedTotal(90_000).WithLogs(logs[0]).TestObject,
            Build.A.Receipt.WithTxType(TxType.Blob).WithStatusCode(1).WithGasUsedTotal(150_000).WithLogs(logs[1]).TestObject,
            Build.A.Receipt.WithTxType(TxType.SetCode).WithStatusCode(1).WithGasUsedTotal(200_000).WithLogs(logs).TestObject,
        ];

        foreach (TxReceipt receipt in receipts)
        {
            if (!eip658) receipt.PostTransactionState = TestItem.KeccakC;
            receipt.Bloom = new Bloom(receipt.Logs);
        }

        return receipts;
    }

    private static byte[] Encode(TxReceipt[] receipts, bool withBloom, bool eip658)
    {
        RlpBehaviors behaviors = eip658 ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None;
        IRlpDecoder<TxReceipt> decoder = withBloom ? Decoder : new ReceiptMessageDecoder69();
        return RlpTestHelper.EncodeSequence([.. receipts.Select(receipt => decoder.Encode(receipt, behaviors))]);
    }
}

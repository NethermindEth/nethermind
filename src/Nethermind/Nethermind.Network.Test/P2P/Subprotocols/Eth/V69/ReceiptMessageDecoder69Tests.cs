// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Test.Encoding;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V69;

[TestFixture]
public class ReceiptMessageDecoder69Tests
{
    // Comfortably under RlpLimit.ReceiptLogs, but more logs than the bytes declaring them could hold.
    private const int UnbackedLogCount = 1_000;

    [Test]
    public void Can_roundtrip_receipt()
    {
        TxReceipt receipt = new()
        {
            TxType = TxType.EIP1559,
            StatusCode = 1,
            GasUsedTotal = 21000,
            Bloom = new Bloom(),
            Logs = []
        };

        ReceiptMessageDecoder69 decoder = new();
        int length = decoder.GetLength(receipt, RlpBehaviors.Eip658Receipts);
        byte[] encoded = new byte[length];
        RlpWriter writer = new(encoded);
        decoder.Encode(ref writer, receipt, RlpBehaviors.Eip658Receipts);

        RlpReader context = new(encoded);
        TxReceipt? decoded = decoder.Decode(ref context, RlpBehaviors.Eip658Receipts);

        Assert.That(decoded, Is.Not.Null);
        Assert.That(decoded!.TxType, Is.EqualTo(receipt.TxType));
        Assert.That(decoded.StatusCode, Is.EqualTo(receipt.StatusCode));
        Assert.That(decoded.GasUsedTotal, Is.EqualTo(receipt.GasUsedTotal));
    }

    [Test]
    public void Decode_throws_on_null_log_entry()
    {
        byte[] encoded = EncodeReceiptWithLogs(1, null);
        ReceiptMessageDecoder69 decoder = new();

        // One byte cannot hold a log entry, so the log-count guard rejects it before the null check.
        Assert.That(Decode, Throws.InstanceOf<RlpException>());

        void Decode()
        {
            RlpReader context = new(encoded);
            decoder.Decode(ref context, RlpBehaviors.Eip658Receipts);
        }
    }

    [Test]
    public void Decode_rejects_a_log_count_the_message_cannot_hold()
    {
        byte[] encoded = EncodeReceiptWithLogs(UnbackedLogCount, null);
        ReceiptMessageDecoder69 decoder = new();

        Assert.That(Decode, Throws.TypeOf<RlpLimitException>());

        void Decode()
        {
            RlpReader context = new(encoded);
            decoder.Decode(ref context, RlpBehaviors.Eip658Receipts);
        }
    }

    [Test]
    public void Decode_accepts_a_log_list_of_smallest_possible_entries()
    {
        byte[] encoded = EncodeReceiptWithLogs(UnbackedLogCount, ReceiptRlpBuilder.MinimalLog());

        RlpReader context = new(encoded);
        TxReceipt? decoded = new ReceiptMessageDecoder69().Decode(ref context, RlpBehaviors.Eip658Receipts);

        Assert.That(decoded!.Logs, Has.Length.EqualTo(UnbackedLogCount));
    }

    [Test]
    public void Encoding_throws_on_null_logs([Values("length", "encode")] string operation)
    {
        TxReceipt receipt = new() { Logs = null };
        ReceiptMessageDecoder69 decoder = new();

        Assert.That(Execute, Throws.TypeOf<RlpException>());

        void Execute()
        {
            if (operation == "length")
            {
                decoder.GetLength(receipt);
                return;
            }

            RlpWriter writer = new(new byte[64]);
            decoder.Encode(ref writer, receipt);
        }
    }

    private static byte[] EncodeReceiptWithLogs(int logCount, LogEntry? log)
    {
        int logLength = log is null ? Rlp.OfEmptyList.Length : Rlp.LengthOf(log);
        int logsLength = logCount * logLength;
        int contentLength = Rlp.LengthOf((byte)TxType.EIP1559)
            + Rlp.LengthOf((byte)1)
            + Rlp.LengthOf(21000UL)
            + Rlp.LengthOfSequence(logsLength);
        byte[] encoded = new byte[Rlp.LengthOfSequence(contentLength)];
        RlpWriter writer = new(encoded);
        writer.StartSequence(contentLength);
        writer.Encode((byte)TxType.EIP1559);
        writer.Encode((byte)1);
        writer.Encode(21000UL);
        writer.StartSequence(logsLength);
        for (int i = 0; i < logCount; i++)
        {
            LogEntryDecoder.Instance.Encode(ref writer, log);
        }

        return encoded;
    }
}

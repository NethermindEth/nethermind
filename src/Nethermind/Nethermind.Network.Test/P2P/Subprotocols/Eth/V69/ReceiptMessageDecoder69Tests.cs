// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Test.Encoding;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V69;

[TestFixture]
public class ReceiptMessageDecoder69Tests
{
    // 23 bytes of data encodes to 47 bytes, so a null placeholder beside it still clears the
    // log-count guard's floor of 24 bytes per entry.
    private static readonly LogEntry PaddingLog = new(Address.Zero, new byte[23], []);

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
        // The padding log buys the null placeholder its 24 bytes of count budget, so the log-count
        // guard passes and the null rejection in the decode loop is what must fire.
        byte[] encoded = EncodeReceipt([null, PaddingLog]);

        Assert.That(() => Decode(encoded), Throws.TypeOf<RlpException>());
    }

    [Test]
    public void Decode_rejects_a_log_count_the_message_cannot_hold()
    {
        byte[] encoded = EncodeReceipt(ReceiptRlpBuilder.Repeat(ReceiptRlpBuilder.UnbackedLogCount));

        Assert.That(() => Decode(encoded), Throws.TypeOf<RlpLimitException>());
    }

    [Test]
    public void Decode_accepts_a_log_list_of_smallest_possible_entries()
    {
        byte[] encoded = EncodeReceipt(ReceiptRlpBuilder.Repeat(ReceiptRlpBuilder.UnbackedLogCount, ReceiptRlpBuilder.MinimalLog()));

        Assert.That(Decode(encoded)!.Logs, Has.Length.EqualTo(ReceiptRlpBuilder.UnbackedLogCount));
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

    private static byte[] EncodeReceipt(ReadOnlySpan<LogEntry?> logs) =>
        ReceiptRlpBuilder.EncodeReceipt69(TxType.EIP1559, logs);

    private static TxReceipt? Decode(byte[] encoded)
    {
        RlpReader context = new(encoded);
        return new ReceiptMessageDecoder69().Decode(ref context, RlpBehaviors.Eip658Receipts);
    }
}

// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V69;

[TestFixture]
public class ReceiptMessageDecoder69Tests
{
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
        byte[] encoded = EncodeReceiptWithNullLogEntry();
        ReceiptMessageDecoder69 decoder = new();

        Assert.That(Decode, Throws.TypeOf<RlpException>());

        void Decode()
        {
            RlpReader context = new(encoded);
            decoder.Decode(ref context, RlpBehaviors.Eip658Receipts);
        }
    }

    private static byte[] EncodeReceiptWithNullLogEntry()
    {
        int logsLength = Rlp.OfEmptyList.Length;
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
        writer.EncodeNullObject();
        return encoded;
    }
}

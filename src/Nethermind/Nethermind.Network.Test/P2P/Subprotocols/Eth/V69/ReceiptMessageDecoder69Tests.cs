// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V69;

[TestFixture]
public class ReceiptMessageDecoder69Tests
{
    private const byte ShortSequenceHeaderBase = 0xc0;

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

    [TestCase("length")]
    [TestCase("encode")]
    public void Encoding_throws_on_null_logs(string operation)
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

    // The item count only requires a log to start before the declared end, so an under-declared logs header
    // consumes the same bytes as the canonical one and every checkpoint one level out still holds.
    [TestCase(false, TestName = "Decode_CanonicalLogsHeader_IsAccepted")]
    [TestCase(true, TestName = "Decode_UnderDeclaredLogsHeader_Throws")]
    public void Decode_LogsHeaderMustMatchItsContent(bool underDeclare)
    {
        LogEntry log = new(TestItem.AddressB, [], []);
        TxReceipt receipt = new()
        {
            TxType = TxType.EIP1559,
            StatusCode = 1,
            GasUsedTotal = 21000,
            Bloom = new Bloom(),
            Logs = [log]
        };

        ReceiptMessageDecoder69 decoder = new();
        byte[] encoded = new byte[decoder.GetLength(receipt, RlpBehaviors.Eip658Receipts)];
        RlpWriter writer = new(encoded);
        decoder.Encode(ref writer, receipt, RlpBehaviors.Eip658Receipts);
        int headerIndex = LogsHeaderIndex(encoded, Rlp.LengthOf(log));

        if (underDeclare)
        {
            encoded[headerIndex]--;
            Assert.That(() => Decode(decoder, encoded), Throws.InstanceOf<RlpException>());
        }
        else
        {
            Assert.That(Decode(decoder, encoded).Logs, Has.Length.EqualTo(1));
        }
    }

    /// <summary>Locates the header of the logs list holding the single log the receipt carries.</summary>
    /// <remarks>The logs are the last item of the payload, so the declared payload end places their header
    /// exactly. A byte scan could instead hit an unrelated field and still throw, passing for the wrong
    /// reason; the header assertion catches any drift in that layout.</remarks>
    private static int LogsHeaderIndex(byte[] encoded, int logsContentLength)
    {
        RlpReader locator = new(encoded);
        int payloadEnd = locator.ReadSequenceLength() + locator.Position;
        int headerIndex = payloadEnd - logsContentLength - 1;
        Assert.That(encoded[headerIndex], Is.EqualTo((byte)(ShortSequenceHeaderBase + logsContentLength)),
            "the logs list header is not where the payload layout puts it");
        return headerIndex;
    }

    private static TxReceipt Decode(ReceiptMessageDecoder69 decoder, byte[] encoded)
    {
        RlpReader reader = new(encoded);
        return decoder.Decode(ref reader, RlpBehaviors.Eip658Receipts)!;
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

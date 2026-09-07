// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding;

public class ReceiptMessageDecoderTests
{
    private const byte ShortSequenceHeaderBase = 0xc0;

    [Test]
    public void TestGlobalReceiptEncoderMustBeReceiptMessageDecoder()
    {
        Rlp.Decoders[typeof(TxReceipt)].Equals(typeof(ReceiptMessageDecoder));
        Rlp.Decoders[typeof(LogEntry)].Equals(typeof(LogEntryDecoder));
    }

    // The item count only requires a log to start before the declared end, so an under-declared logs header
    // consumes the same bytes as the canonical one and every checkpoint one level out still holds.
    [TestCase(TxType.Legacy, false, TestName = "Decode_CanonicalLogsHeader_IsAccepted")]
    [TestCase(TxType.Legacy, true, TestName = "Decode_UnderDeclaredLogsHeader_Throws")]
    [TestCase(TxType.FrameTx, false, TestName = "Decode_CanonicalFrameLogsHeader_IsAccepted")]
    [TestCase(TxType.FrameTx, true, TestName = "Decode_UnderDeclaredFrameLogsHeader_Throws")]
    public void Decode_LogsHeaderMustMatchItsContent(TxType txType, bool underDeclare)
    {
        LogEntry log = new(TestItem.AddressB, [], []);
        TxReceipt receipt = txType == TxType.FrameTx
            ? new TxReceipt
            {
                TxType = TxType.FrameTx,
                GasUsedTotal = 21_000,
                Payer = TestItem.AddressA,
                FrameReceipts = [new TxFrameReceipt(TxFrameReceipt.StatusSuccess, 21_000, 0, [log])],
            }
            : new TxReceipt
            {
                TxType = TxType.Legacy,
                StatusCode = 1,
                GasUsedTotal = 21_000,
                Bloom = new Bloom(),
                Logs = [log],
            };

        ReceiptMessageDecoder decoder = new();
        byte[] encoded = decoder.EncodeNew(receipt, RlpBehaviors.Eip658Receipts);
        int headerIndex = LogsHeaderIndex(encoded, txType, Rlp.LengthOf(log));

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

    /// <summary>Locates the header of the logs list holding the single log both paths encode.</summary>
    /// <remarks>The logs are the last item of the payload on either path, so the declared payload end places
    /// their header exactly. A byte scan could instead hit an unrelated field and still throw, passing for the
    /// wrong reason; the header assertion catches any drift in that layout.</remarks>
    private static int LogsHeaderIndex(byte[] encoded, TxType txType, int logsContentLength)
    {
        RlpReader locator = new(encoded);
        if (txType != TxType.Legacy)
        {
            locator.SkipLength();
            locator.ReadByte();
        }

        int payloadEnd = locator.ReadSequenceLength() + locator.Position;
        int headerIndex = payloadEnd - logsContentLength - 1;
        Assert.That(encoded[headerIndex], Is.EqualTo((byte)(ShortSequenceHeaderBase + logsContentLength)),
            "the logs list header is not where the payload layout puts it");
        return headerIndex;
    }

    private static TxReceipt Decode(ReceiptMessageDecoder decoder, byte[] encoded)
    {
        RlpReader reader = new(encoded);
        return decoder.Decode(ref reader)!;
    }
}

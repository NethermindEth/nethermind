// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding;

public class ReceiptMessageDecoderTests
{
    // Logs bound at the default 1 GGas MaxBlockGas:
    // 1,000,000,000 / GasCostOf.Log (375) + 1 == 2,666,667 entries.
    private const int LogCountLimit = 2_666_667;

    [Test]
    public void TestGlobalReceiptEncoderMustBeReceiptMessageDecoder()
    {
        Rlp.Decoders[typeof(TxReceipt)].Equals(typeof(ReceiptMessageDecoder));
        Rlp.Decoders[typeof(LogEntry)].Equals(typeof(LogEntryDecoder));
    }

    [Test]
    public void Log_count_limit_derives_from_the_block_gas_ceiling() =>
        Assert.That(RlpLimit.ReceiptLogs.Limit, Is.EqualTo(LogCountLimit));

    // RlpLimitException fires before any log is decoded, so 0xC0 placeholders are fine here; at the
    // limit decoding proceeds and the placeholder is rejected as a null log instead.
    [TestCase(LogCountLimit + 1, typeof(RlpLimitException), TestName = "log count over limit")]
    [TestCase(LogCountLimit, typeof(RlpException), TestName = "log count at limit")]
    public void Decode_log_count_throws(int logCount, Type expected) =>
        Assert.Throws(expected, () => DecodeReceipt(BuildReceiptStream(logCount)));

    private static void DecodeReceipt(byte[] bytes)
    {
        RlpReader ctx = new(bytes);
        new ReceiptMessageDecoder().DecodeGuardNotNull(ref ctx);
    }

    private static byte[] BuildReceiptStream(int logCount)
    {
        int contentLength = Rlp.LengthOf((byte)1)
                            + Rlp.LengthOf(0UL)
                            + Rlp.LengthOf(Bloom.Empty)
                            + Rlp.LengthOfSequence(logCount);

        byte[] bytes = new byte[Rlp.LengthOfSequence(contentLength)];
        RlpWriter writer = new(bytes);
        writer.StartSequence(contentLength);
        writer.Encode((byte)1);
        writer.Encode(0UL);
        writer.Encode(Bloom.Empty);
        writer.StartSequence(logCount);
        for (int i = 0; i < logCount; i++)
            writer.StartSequence(0); // 0xC0 - empty-list placeholder (decodes as null)

        return bytes;
    }
}

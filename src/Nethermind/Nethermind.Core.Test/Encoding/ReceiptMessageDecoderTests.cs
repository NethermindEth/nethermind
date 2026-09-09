// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

    [Test]
    public void Min_encoded_log_length_matches_the_smallest_encodable_log() =>
        Assert.That(Rlp.LengthOf(ReceiptRlpBuilder.MinimalLog()), Is.EqualTo(LogEntryDecoder.MinEncodedLength));

    [Test]
    public void Decode_rejects_a_log_count_the_message_cannot_hold()
    {
        byte[] encoded = ReceiptRlpBuilder.EncodeReceipt(ReceiptRlpBuilder.Repeat(ReceiptRlpBuilder.UnbackedLogCount));

        Assert.Throws<RlpLimitException>(() => DecodeReceipt(encoded));
    }

    [Test]
    public void Decode_accepts_a_log_list_of_smallest_possible_entries()
    {
        byte[] encoded = ReceiptRlpBuilder.EncodeReceipt(ReceiptRlpBuilder.Repeat(ReceiptRlpBuilder.UnbackedLogCount, ReceiptRlpBuilder.MinimalLog()));

        Assert.That(DecodeReceipt(encoded).Logs, Has.Length.EqualTo(ReceiptRlpBuilder.UnbackedLogCount));
    }

    private static TxReceipt DecodeReceipt(byte[] bytes)
    {
        RlpReader ctx = new(bytes);
        return new ReceiptMessageDecoder().DecodeGuardNotNull(ref ctx);
    }
}

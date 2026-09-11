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

    [Test]
    public void Min_encoded_log_length_matches_the_smallest_encodable_log() =>
        Assert.That(Rlp.LengthOf(ReceiptRlpBuilder.MinimalLog()), Is.EqualTo(LogEntryDecoder.MinNonNullEncodedLength));

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

    internal static TxReceipt DecodeReceipt(byte[] bytes)
    {
        RlpReader ctx = new(bytes);
        return new ReceiptMessageDecoder().DecodeGuardNotNull(ref ctx);
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


/// <summary>Covers the count arm of the log guard, which only binds below the byte arm.</summary>
/// <remarks>Separate fixture because <see cref="RlpLimit.InitMaxBlockGas"/> writes process-global state.</remarks>
[NonParallelizable]
public class ReceiptLogCountLimitTests
{
    // A two-log ceiling derives a limit of 750 / GasCostOf.Log + 1 == 3 logs.
    private const ulong TwoLogBlockGas = GasCostOf.Log * 2;
    private const int TwoLogBlockGasLimit = 3;

    private ulong _maxBlockGas;

    [SetUp]
    public void RecordBlockGas() => _maxBlockGas = RlpLimit.MaxBlockGas;

    [TearDown]
    public void RestoreBlockGas() => RlpLimit.InitMaxBlockGas(_maxBlockGas);

    // 1 GGas is the BlocksConfig.MaxGasLimit default; 30M a typical mainnet-era ceiling.
    [TestCase(1_000_000_000ul, 2_666_667)]
    [TestCase(30_000_000ul, 80_001)]
    [TestCase(TwoLogBlockGas, TwoLogBlockGasLimit)]
    // Clamped one below int.MaxValue so that a Limit + 1 peek cannot wrap negative.
    [TestCase(ulong.MaxValue, int.MaxValue - 1)]
    public void Log_count_limit_derives_from_the_block_gas_ceiling(ulong maxBlockGas, int expectedLimit)
    {
        RlpLimit.InitMaxBlockGas(maxBlockGas);

        Assert.That(RlpLimit.ReceiptLogs.Limit, Is.EqualTo(expectedLimit));
    }

    [Test]
    public void Decode_rejects_a_log_count_above_the_gas_ceiling()
    {
        // Every log is backed by its own 24 bytes, so only the gas ceiling can reject this count.
        byte[] encoded = ReceiptRlpBuilder.EncodeReceipt(
            ReceiptRlpBuilder.Repeat(TwoLogBlockGasLimit + 1, ReceiptRlpBuilder.MinimalLog()));
        Assert.That(ReceiptMessageDecoderTests.DecodeReceipt(encoded).Logs, Has.Length.EqualTo(TwoLogBlockGasLimit + 1));

        RlpLimit.InitMaxBlockGas(TwoLogBlockGas);

        Assert.Throws<RlpLimitException>(() => ReceiptMessageDecoderTests.DecodeReceipt(encoded));
    }
}

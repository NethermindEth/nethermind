// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding;

public class ReceiptMessageDecoderTests
{
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

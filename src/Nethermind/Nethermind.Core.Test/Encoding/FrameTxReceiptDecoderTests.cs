// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding;

/// <summary>
/// Round-trips of the EIP-8141 receipt payload (no top-level status or bloom on the wire): the
/// decoder derives StatusCode from the frame statuses and unions the frame logs into Logs.
/// </summary>
[TestFixture]
public class FrameTxReceiptDecoderTests
{
    [TestCaseSource(nameof(RoundtripCases))]
    public void Roundtrip_FrameTxReceipt_PreservesPayloadFields(TxReceipt receipt, byte expectedStatus)
    {
        ReceiptMessageDecoder decoder = new();

        byte[] encoded = decoder.EncodeNew(receipt, RlpBehaviors.None);
        RlpReader reader = new(encoded);
        TxReceipt decoded = decoder.Decode(ref reader)!;

        Assert.That(decoded.GasUsedTotal, Is.EqualTo(receipt.GasUsedTotal));
        Assert.That(decoded.Payer, Is.EqualTo(receipt.Payer));
        AssertFrameReceiptsEqual(decoded.FrameReceipts!, receipt.FrameReceipts!);
        Assert.That(decoded.StatusCode, Is.EqualTo(expectedStatus),
            "the transaction status is absent from the wire and must be derived from the frame statuses");
        AssertLogsEqual(decoded.Logs!, receipt.FrameReceipts!.SelectMany(static f => f.Logs).ToArray());
    }

    // Storage keeps the union Logs and the per-frame logs as independent fields; the union here is
    // deliberately not the frame-order concatenation, pinning that Logs is read back verbatim.
    [Test]
    public void StorageRoundtrip_PreservesPayerFrameReceiptsAndUnionLogs(
        [Values(true, false)] bool compactEncoding)
    {
        LogEntry unionLog = Log(0x01);
        LogEntry frameOnlyLog = Log(0x02);
        TxReceipt frameReceipt = CreateStorageFrameReceipt(
            [unionLog],
            new TxFrameReceipt(TxFrameReceipt.StatusSuccess, 21_000, 5_000, [unionLog]),
            new TxFrameReceipt(TxFrameReceipt.StatusFailure, 30_000, 0, [frameOnlyLog]),
            new TxFrameReceipt(TxFrameReceipt.StatusSkipped, 0, 0, []));
        TxReceipt legacyReceipt = Build.A.Receipt.WithAllFieldsFilled.WithCalculatedBloom().TestObject;

        ReceiptArrayStorageDecoder encoder = new(compactEncoding);
        using Nethermind.Core.Collections.ArrayPoolSpan<byte> rlp =
            encoder.EncodeToArrayPoolSpan([legacyReceipt, frameReceipt], RlpBehaviors.Storage | RlpBehaviors.Eip658Receipts);

        RlpReader ctx = new((System.ReadOnlySpan<byte>)rlp);
        TxReceipt[] decoded = ReceiptArrayStorageDecoder.Instance.Decode(ref ctx, RlpBehaviors.Storage);

        Assert.That(decoded, Has.Length.EqualTo(2));
        Assert.That(decoded[0].Payer, Is.Null, "regular receipts carry no frame extension");
        Assert.That(decoded[0].FrameReceipts, Is.Null);

        TxReceipt decodedFrame = decoded[1];
        Assert.That(decodedFrame.TxType, Is.EqualTo(TxType.FrameTx));
        Assert.That(decodedFrame.Payer, Is.EqualTo(frameReceipt.Payer));
        AssertFrameReceiptsEqual(decodedFrame.FrameReceipts!, frameReceipt.FrameReceipts!);
        AssertLogsEqual(decodedFrame.Logs!, frameReceipt.Logs!,
            "the stored union must stay the union, not get rebuilt from frame logs");
    }

    // ReceiptsIterator (eth_getLogs) loops DecodeStructRef over stored receipts; a frame-tx receipt
    // used to leave the reader mid-sequence and corrupt the next one, so it sits between two regulars.
    [Test]
    public void StructRefIteration_OverArrayWithFrameTxReceipt_DoesNotThrowOrCorruptNeighbours(
        [Values(true, false)] bool compactEncoding,
        [Values(RlpBehaviors.Storage, RlpBehaviors.Storage | RlpBehaviors.AllowExtraBytes)] RlpBehaviors decodeBehaviors)
    {
        LogEntry frameLog = Log(0x01);
        TxReceipt frameReceipt = CreateStorageFrameReceipt(
            [frameLog],
            new TxFrameReceipt(TxFrameReceipt.StatusSuccess, 21_000, 5_000, [frameLog]),
            new TxFrameReceipt(TxFrameReceipt.StatusFailure, 30_000, 0, [Log(0x02)]));

        // Distinct sender/gas on the neighbours so realigning onto `after` is provably not `before`.
        TxReceipt before = Build.A.Receipt.WithAllFieldsFilled
            .WithSender(TestItem.AddressD).WithGasUsedTotal(1000).WithCalculatedBloom().TestObject;
        TxReceipt after = Build.A.Receipt.WithAllFieldsFilled
            .WithSender(TestItem.AddressE).WithGasUsedTotal(2000).WithCalculatedBloom().TestObject;

        RlpDecoder<TxReceipt> decoder = compactEncoding
            ? new CompactReceiptStorageDecoder()
            : new ReceiptStorageDecoder();
        IReceiptRefDecoder refDecoder = (IReceiptRefDecoder)decoder;
        byte[] encoded = decoder.Encode([before, frameReceipt, after], RlpBehaviors.Storage | RlpBehaviors.Eip658Receipts).Bytes;

        // Iterate while Position is below the sequence content length. TxReceiptStructRef is a ref
        // struct, so capture the asserted fields into a tuple.
        RlpReader reader = new(encoded);
        int length = reader.ReadSequenceLength();
        int count = 0;
        (byte Status, ulong Gas, string Sender, TxType Type, LogEntry[] Logs)[] decoded = new (byte, ulong, string, TxType, LogEntry[])[3];
        while (reader.Position < length)
        {
            refDecoder.DecodeStructRef(ref reader, decodeBehaviors, out TxReceiptStructRef current);
            if (count < decoded.Length)
            {
                decoded[count] = (current.StatusCode, current.GasUsedTotal, current.Sender.ToString(), current.TxType, DecodeLogs(current.LogsRlp, compactEncoding));
            }
            count++;
        }

        Assert.That(count, Is.EqualTo(3), "every receipt must decode, including the neighbours");

        Assert.That(decoded[0].Sender, Is.EqualTo(before.Sender!.ToString()), "leading receipt sender");
        Assert.That(decoded[0].Gas, Is.EqualTo(before.GasUsedTotal), "leading receipt gas used total");
        Assert.That(decoded[0].Type, Is.EqualTo(TxType.Legacy), "a receipt without the extension must not be labelled FrameTx");

        Assert.That(decoded[1].Status, Is.EqualTo(frameReceipt.StatusCode), "frame status");
        Assert.That(decoded[1].Gas, Is.EqualTo(frameReceipt.GasUsedTotal), "frame gas used total");
        Assert.That(decoded[1].Sender, Is.EqualTo(frameReceipt.Sender!.ToString()), "frame sender");
        // TxType here is decoder-assigned and only observed by callers that skip recovery; on
        // eth_getLogs recovery overwrites it from the matching transaction.
        Assert.That(decoded[1].Type, Is.EqualTo(TxType.FrameTx), "the frame-tx receipt must be typed FrameTx");
        AssertLogsEqual(decoded[1].Logs, frameReceipt.Logs!);

        // The trailing receipt decodes intact only if the reader advanced past the frame extension.
        Assert.That(decoded[2].Sender, Is.EqualTo(after.Sender!.ToString()), "trailing receipt sender");
        Assert.That(decoded[2].Gas, Is.EqualTo(after.GasUsedTotal), "trailing receipt gas used total");
        Assert.That(decoded[2].Type, Is.EqualTo(TxType.Legacy));
    }

    private static LogEntry[] DecodeLogs(scoped ReadOnlySpan<byte> logsRlp, bool compact)
    {
        RlpReader reader = new(logsRlp);
        int end = reader.ReadSequenceLength() + reader.Position;
        List<LogEntry> logs = [];
        while (reader.Position < end)
        {
            LogEntry log = compact
                ? CompactLogEntryDecoder.Instance.Decode(ref reader, RlpBehaviors.AllowExtraBytes)!
                : LogEntryDecoder.Instance.Decode(ref reader, RlpBehaviors.AllowExtraBytes)!;
            logs.Add(log);
        }

        return logs.ToArray();
    }

    private static IEnumerable<TestCaseData> RoundtripCases()
    {
        yield return new TestCaseData(CreateReceipt(
            new TxFrameReceipt(TxFrameReceipt.StatusSuccess, 21_000, 5_000, [Log(0x01)])),
            TxFrameReceipt.StatusSuccess)
            .SetName("Roundtrip_SingleSuccessfulFrameWithLog");

        yield return new TestCaseData(CreateReceipt(
            new TxFrameReceipt(TxFrameReceipt.StatusSuccess, 50_000, 9_000, [Log(0x01), Log(0x02)]),
            new TxFrameReceipt(TxFrameReceipt.StatusFailure, 30_000, 0, []),
            new TxFrameReceipt(TxFrameReceipt.StatusSkipped, 0, 0, [])),
            TxFrameReceipt.StatusFailure)
            .SetName("Roundtrip_SuccessFailureAndSkippedStatuses");

        // A frame skipped by a failed atomic batch is not a success either.
        yield return new TestCaseData(CreateReceipt(
            new TxFrameReceipt(TxFrameReceipt.StatusSuccess, 21_000, 5_000, []),
            new TxFrameReceipt(TxFrameReceipt.StatusSkipped, 0, 0, [])),
            TxFrameReceipt.StatusFailure)
            .SetName("Roundtrip_SkippedFrameIsNotASuccess");

        yield return new TestCaseData(CreateReceipt(
            new TxFrameReceipt(TxFrameReceipt.StatusSuccess, 0, 0, [])),
            TxFrameReceipt.StatusSuccess)
            .SetName("Roundtrip_EmptyLogsAndZeroGas");
    }

    private static void AssertFrameReceiptsEqual(TxFrameReceipt[] actual, TxFrameReceipt[] expected)
    {
        Assert.That(actual.Length, Is.EqualTo(expected.Length));
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.That(actual[i].Status, Is.EqualTo(expected[i].Status), $"frame receipt {i} status");
            Assert.That(actual[i].ExecutionGasUsed, Is.EqualTo(expected[i].ExecutionGasUsed), $"frame receipt {i} execution gas used");
            Assert.That(actual[i].StateGasUsed, Is.EqualTo(expected[i].StateGasUsed), $"frame receipt {i} state gas used");
            AssertLogsEqual(actual[i].Logs, expected[i].Logs);
        }
    }

    // LogEntry has no value equality, so logs are compared field by field.
    private static void AssertLogsEqual(LogEntry[] actual, LogEntry[] expected, string? message = null)
    {
        Assert.That(actual.Length, Is.EqualTo(expected.Length), message);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.That(actual[i].Address, Is.EqualTo(expected[i].Address), $"log {i} address");
            Assert.That(actual[i].Data.ToArray(), Is.EqualTo(expected[i].Data.ToArray()), $"log {i} data");
            Assert.That(actual[i].Topics, Is.EqualTo(expected[i].Topics), $"log {i} topics");
        }
    }

    private static TxReceipt CreateReceipt(params TxFrameReceipt[] frameReceipts) =>
        new()
        {
            TxType = TxType.FrameTx,
            GasUsedTotal = frameReceipts.Aggregate(0UL, static (sum, f) => sum + f.GasUsed),
            Payer = TestItem.AddressA,
            FrameReceipts = frameReceipts,
        };

    // Storage-only fixups a frame receipt gets before persistence: status, sender, union Logs, bloom.
    private static TxReceipt CreateStorageFrameReceipt(LogEntry[] unionLogs, params TxFrameReceipt[] frameReceipts)
    {
        TxReceipt receipt = CreateReceipt(frameReceipts);
        receipt.StatusCode = TxFrameReceipt.StatusSuccess;
        receipt.Sender = TestItem.AddressC;
        receipt.Logs = unionLogs;
        receipt.Bloom = new Bloom(unionLogs);
        return receipt;
    }

    private static LogEntry Log(byte marker) =>
        new(TestItem.AddressB, [marker], [Keccak.Compute([marker])]);
}

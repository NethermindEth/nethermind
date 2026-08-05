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
/// Round-trips of the EIP-8141 receipt payload <c>[cumulative_gas_used, payer,
/// [[status, gas_used, logs], ...]]</c>. The wire form is spec-literal: no top-level status and no
/// bloom. EIP8141-GAP: internally the decoder sets StatusCode to success and unions frame logs into
/// Logs so bloom calculation and log indexing keep working — asserted here as the pinned behavior.
/// </summary>
[TestFixture]
public class FrameTxReceiptDecoderTests
{
    [TestCaseSource(nameof(RoundtripCases))]
    public void Roundtrip_FrameTxReceipt_PreservesPayloadFields(TxReceipt receipt)
    {
        ReceiptMessageDecoder decoder = new();

        byte[] encoded = decoder.EncodeNew(receipt, RlpBehaviors.None);
        RlpReader reader = new(encoded);
        TxReceipt decoded = decoder.Decode(ref reader)!;

        Assert.That(decoded.GasUsedTotal, Is.EqualTo(receipt.GasUsedTotal));
        Assert.That(decoded.Payer, Is.EqualTo(receipt.Payer));
        AssertFrameReceiptsEqual(decoded.FrameReceipts!, receipt.FrameReceipts!);
        Assert.That(decoded.StatusCode, Is.EqualTo(TxFrameReceipt.StatusSuccess));
        // Decoded Logs must be the in-order union of frame logs.
        AssertLogsEqual(decoded.Logs!, receipt.FrameReceipts!.SelectMany(static f => f.Logs).ToArray());
    }

    // Storage persists the union Logs and the per-frame logs as independent fields (unlike the wire
    // form, which rebuilds the union from the frame logs on decode). The union here is deliberately
    // NOT the frame-order concatenation, pinning that the decoder reads Logs back verbatim.
    [Test]
    public void StorageRoundtrip_PreservesPayerFrameReceiptsAndUnionLogs(
        [Values(true, false)] bool compactEncoding)
    {
        LogEntry unionLog = Log(0x01);
        LogEntry frameOnlyLog = Log(0x02);
        // Union omits frameOnlyLog, so it diverges from the frame-order concatenation on purpose.
        TxReceipt frameReceipt = CreateStorageFrameReceipt(
            [unionLog],
            new TxFrameReceipt(TxFrameReceipt.StatusSuccess, 21_000, [unionLog]),
            new TxFrameReceipt(TxFrameReceipt.StatusFailure, 30_000, [frameOnlyLog]),
            new TxFrameReceipt(TxFrameReceipt.StatusSkipped, 0, []));
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

    // Regression: ReceiptsIterator (eth_getLogs) loops DecodeStructRef with RlpBehaviors.Storage.
    // A frame-tx receipt used to leave the reader mid-sequence, breaking the next receipt. It sits
    // between two regular ones so any misalignment surfaces on a neighbour.
    [Test]
    public void StructRefIteration_OverArrayWithFrameTxReceipt_DoesNotThrowOrCorruptNeighbours()
    {
        LogEntry frameLog = Log(0x01);
        TxReceipt frameReceipt = CreateStorageFrameReceipt(
            [frameLog],
            new TxFrameReceipt(TxFrameReceipt.StatusSuccess, 21_000, [frameLog]),
            new TxFrameReceipt(TxFrameReceipt.StatusFailure, 30_000, [Log(0x02)]));

        // Distinct sender/gas on the neighbours so realigning onto `after` is provably not `before`.
        TxReceipt before = Build.A.Receipt.WithAllFieldsFilled
            .WithSender(TestItem.AddressD).WithGasUsedTotal(1000).WithCalculatedBloom().TestObject;
        TxReceipt after = Build.A.Receipt.WithAllFieldsFilled
            .WithSender(TestItem.AddressE).WithGasUsedTotal(2000).WithCalculatedBloom().TestObject;

        CompactReceiptStorageDecoder decoder = new();
        byte[] encoded = decoder.Encode([before, frameReceipt, after], RlpBehaviors.Storage | RlpBehaviors.Eip658Receipts).Bytes;

        // Mirror ReceiptsIterator.TryGetNext's loop bound exactly. TxReceiptStructRef is a ref
        // struct, so capture the scalar fields we assert per index as we iterate.
        RlpReader reader = new(encoded);
        int length = reader.ReadSequenceLength();
        int count = 0;
        (byte Status, ulong Gas, string Sender, TxType Type, LogEntry[] Logs)[] decoded = new (byte, ulong, string, TxType, LogEntry[])[3];
        while (reader.Position < length)
        {
            decoder.DecodeStructRef(ref reader, RlpBehaviors.Storage, out TxReceiptStructRef current);
            if (count < decoded.Length)
            {
                decoded[count] = (current.StatusCode, current.GasUsedTotal, current.Sender.ToString(), current.TxType, DecodeLogs(current.LogsRlp));
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
        Assert.That(decoded[1].Type, Is.EqualTo(TxType.FrameTx), "the extension's presence identifies a frame-tx receipt");
        AssertLogsEqual(decoded[1].Logs, frameReceipt.Logs!);

        // The receipt after the frame tx must be intact, proving the reader realigned.
        Assert.That(decoded[2].Sender, Is.EqualTo(after.Sender!.ToString()), "trailing receipt sender");
        Assert.That(decoded[2].Gas, Is.EqualTo(after.GasUsedTotal), "trailing receipt gas used total");
        Assert.That(decoded[2].Type, Is.EqualTo(TxType.Legacy));
    }

    private static LogEntry[] DecodeLogs(scoped ReadOnlySpan<byte> logsRlp)
    {
        RlpReader reader = new(logsRlp);
        int end = reader.ReadSequenceLength() + reader.Position;
        List<LogEntry> logs = [];
        while (reader.Position < end)
        {
            LogEntry log = CompactLogEntryDecoder.Instance.Decode(ref reader, RlpBehaviors.AllowExtraBytes)!;
            logs.Add(log);
        }

        return logs.ToArray();
    }

    private static IEnumerable<TestCaseData> RoundtripCases()
    {
        yield return new TestCaseData(CreateReceipt(
            new TxFrameReceipt(TxFrameReceipt.StatusSuccess, 21_000, [Log(0x01)])))
            .SetName("Roundtrip_SingleSuccessfulFrameWithLog");

        yield return new TestCaseData(CreateReceipt(
            new TxFrameReceipt(TxFrameReceipt.StatusSuccess, 50_000, [Log(0x01), Log(0x02)]),
            new TxFrameReceipt(TxFrameReceipt.StatusFailure, 30_000, []),
            new TxFrameReceipt(TxFrameReceipt.StatusSkipped, 0, [])))
            .SetName("Roundtrip_SuccessFailureAndSkippedStatuses");

        yield return new TestCaseData(CreateReceipt(
            new TxFrameReceipt(TxFrameReceipt.StatusSuccess, 0, [])))
            .SetName("Roundtrip_EmptyLogsAndZeroGas");
    }

    private static void AssertFrameReceiptsEqual(TxFrameReceipt[] actual, TxFrameReceipt[] expected)
    {
        Assert.That(actual.Length, Is.EqualTo(expected.Length));
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.That(actual[i].Status, Is.EqualTo(expected[i].Status), $"frame receipt {i} status");
            Assert.That(actual[i].GasUsed, Is.EqualTo(expected[i].GasUsed), $"frame receipt {i} gas used");
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

    // Applies the storage-only fixups a frame receipt gets before persistence: success status,
    // recovered sender, verbatim union Logs, and matching bloom.
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

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

    // The storage form appends [payer, [frame_receipt, ...]] after the standard fields, so a
    // restart round-trips the execution results the block cannot reproduce. The union Logs and the
    // per-frame logs are stored independently — a rolled-back batch frame keeps its log in the
    // frame receipt while the union omits it, and storage must preserve exactly that divergence.
    [Test]
    public void StorageRoundtrip_PreservesPayerFrameReceiptsAndUnionLogs(
        [Values(true, false)] bool compactEncoding)
    {
        LogEntry unionLog = Log(0x01);
        LogEntry rolledBackLog = Log(0x02);
        TxReceipt frameReceipt = CreateReceipt(
            new TxFrameReceipt(TxFrameReceipt.StatusSuccess, 21_000, [unionLog]),
            new TxFrameReceipt(TxFrameReceipt.StatusFailure, 30_000, [rolledBackLog]),
            new TxFrameReceipt(TxFrameReceipt.StatusSkipped, 0, []));
        frameReceipt.StatusCode = TxFrameReceipt.StatusSuccess;
        frameReceipt.Sender = TestItem.AddressC;
        frameReceipt.Logs = [unionLog];
        frameReceipt.Bloom = new Bloom(frameReceipt.Logs);
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
        AssertLogsEqual(decodedFrame.Logs!, frameReceipt.Logs,
            "the stored union must stay the union, not get rebuilt from frame logs");
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

    private static LogEntry Log(byte marker) =>
        new(TestItem.AddressB, [marker], [Keccak.Compute([marker])]);
}

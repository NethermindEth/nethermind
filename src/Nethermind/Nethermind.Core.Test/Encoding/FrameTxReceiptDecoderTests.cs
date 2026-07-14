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
        Assert.That(decoded.Logs, Is.EqualTo(receipt.FrameReceipts!.SelectMany(static f => f.Logs).ToArray()),
            "decoded Logs must be the in-order union of frame logs");
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
            Assert.That(actual[i].Logs, Is.EqualTo(expected[i].Logs), $"frame receipt {i} logs");
        }
    }

    private static TxReceipt CreateReceipt(params TxFrameReceipt[] frameReceipts) =>
        new()
        {
            TxType = TxType.FrameTx,
            GasUsedTotal = (long)frameReceipts.Aggregate(0UL, static (sum, f) => sum + f.GasUsed),
            Payer = TestItem.AddressA,
            FrameReceipts = frameReceipts,
        };

    private static LogEntry Log(byte marker) =>
        new(TestItem.AddressB, [marker], [Keccak.Compute([marker])]);
}

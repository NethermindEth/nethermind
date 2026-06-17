// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.EraE.Test;

public class ReceiptMessageDecoderSlimTests
{
    private static readonly ReceiptMessageDecoder SlimDecoder = new(skipBloom: true);
    private static readonly ReceiptMessageDecoder FullDecoder = new();

    [Test]
    public void Encode_WithSkipBloom_ProducesSmallerPayloadThanFull()
    {
        TxReceipt receipt = Build.A.Receipt
            .WithTxType(TxType.EIP1559)
            .WithLogs(Build.A.LogEntry.WithData([1, 2, 3]).TestObject)
            .TestObject;

        byte[] slimBytes = SlimDecoder.EncodeNew(receipt, RlpBehaviors.Eip658Receipts);
        byte[] fullBytes = FullDecoder.EncodeNew(receipt, RlpBehaviors.Eip658Receipts);

        Assert.That(slimBytes.Length, Is.LessThan(fullBytes.Length), "slim receipt omits the 256-byte bloom filter");
    }

    [Test]
    public void Decode_AfterSlimEncode_PreservesStatusGasAndLogs()
    {
        LogEntry log = Build.A.LogEntry
            .WithAddress(TestItem.AddressA)
            .WithData([0xDE, 0xAD, 0xBE, 0xEF])
            .TestObject;

        TxReceipt original = Build.A.Receipt
            .WithTxType(TxType.EIP1559)
            .WithStatusCode(1)
            .WithGasUsed(21000)
            .WithLogs(log)
            .TestObject;

        byte[] encoded = SlimDecoder.EncodeNew(original, RlpBehaviors.Eip658Receipts);

        RlpReader ctx = new(encoded.AsSpan());
        TxReceipt decoded = SlimDecoder.Decode(ref ctx, RlpBehaviors.Eip658Receipts)!;

        Assert.That(decoded.StatusCode, Is.EqualTo(original.StatusCode));
        Assert.That(decoded.GasUsedTotal, Is.EqualTo(original.GasUsedTotal));
        Assert.That(decoded.Logs!, Has.Length.EqualTo(original.Logs!.Length));
        Assert.That(decoded.Logs[0].Data, Is.EqualTo(original.Logs[0].Data));
    }

    [Test]
    public void Decode_WithSlimEncodedReceipt_BloomIsReconstructedFromLogs()
    {
        LogEntry log = Build.A.LogEntry
            .WithAddress(TestItem.AddressA)
            .WithTopics(TestItem.KeccakA)
            .TestObject;

        TxReceipt original = Build.A.Receipt
            .WithTxType(TxType.EIP1559)
            .WithStatusCode(1)
            .WithLogs(log)
            .TestObject;

        byte[] encoded = SlimDecoder.EncodeNew(original, RlpBehaviors.Eip658Receipts);
        RlpReader ctx = new(encoded.AsSpan());
        TxReceipt decoded = SlimDecoder.Decode(ref ctx, RlpBehaviors.Eip658Receipts)!;

        Assert.That(decoded.Bloom, Is.Not.Null);
        Assert.That(decoded.Bloom!, Is.EqualTo(original.Bloom), "bloom must be identical to what the full receipt would have");
    }

    [Test]
    public void Encode_WithEmptyLogs_DecodesWithoutError()
    {
        TxReceipt receipt = Build.A.Receipt
            .WithTxType(TxType.Legacy)
            .WithLogs()
            .TestObject;

        byte[] encoded = SlimDecoder.EncodeNew(receipt, RlpBehaviors.Eip658Receipts);
        RlpReader ctx = new(encoded.AsSpan());
        TxReceipt decoded = SlimDecoder.Decode(ref ctx, RlpBehaviors.Eip658Receipts)!;

        Assert.That(decoded, Is.Not.Null);
        Assert.That(decoded.Logs, Is.Not.Null, "decoder must return an empty array, not null");
        Assert.That(decoded.Logs!, Is.Empty);
    }
}

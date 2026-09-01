// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding;

/// <summary>Round-trips of the EIP-8141 receipt payload (no top-level status or bloom on the wire): the decoder
/// derives StatusCode from the frame statuses and unions the frame logs into Logs.</summary>
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

    /// <summary>The object-path array decode over LegacyStorage receipts must both decode a frame-tx receipt's
    /// [payer, per-frame receipts] extension and realign to its own end, mirroring the compact object path; otherwise
    /// under AllowExtraBytes the extension is read as the next element and corrupts it. The frame receipt sits between
    /// two distinct regulars so realigning onto the trailing one is provably not the leading one, and the asserted
    /// Payer/FrameReceipts prove the extension is decoded rather than skipped.</summary>
    [Test]
    public void ArrayDecode_ObjectPath_FrameTxReceiptBetweenRegulars_DecodesExtensionAndStaysAligned(
        [Values(RlpBehaviors.Storage, RlpBehaviors.Storage | RlpBehaviors.AllowExtraBytes)] RlpBehaviors decodeBehaviors)
    {
        LogEntry frameLog = Log(0x01);
        TxReceipt frameReceipt = CreateStorageFrameReceipt(
            [frameLog],
            new TxFrameReceipt(TxFrameReceipt.StatusSuccess, 21_000, 5_000, [frameLog]),
            new TxFrameReceipt(TxFrameReceipt.StatusFailure, 30_000, 0, [Log(0x02)]));

        TxReceipt before = Build.A.Receipt.WithAllFieldsFilled
            .WithSender(TestItem.AddressD).WithGasUsedTotal(1000).WithCalculatedBloom().TestObject;
        TxReceipt after = Build.A.Receipt.WithAllFieldsFilled
            .WithSender(TestItem.AddressE).WithGasUsedTotal(2000).WithCalculatedBloom().TestObject;

        ReceiptStorageDecoder decoder = new();
        byte[] encoded = decoder.Encode([before, frameReceipt, after], RlpBehaviors.Storage | RlpBehaviors.Eip658Receipts).Bytes;

        RlpReader reader = new(encoded);
        TxReceipt[] decoded = decoder.DecodeArray(ref reader, decodeBehaviors);

        Assert.That(decoded, Has.Length.EqualTo(3), "every receipt must decode, including the neighbour after the frame extension");

        Assert.That(decoded[0].Sender, Is.EqualTo(before.Sender), "leading receipt sender");
        Assert.That(decoded[0].GasUsedTotal, Is.EqualTo(before.GasUsedTotal), "leading receipt gas used total");
        Assert.That(decoded[0].TxType, Is.EqualTo(TxType.Legacy), "a receipt without the extension must not be labelled FrameTx");

        Assert.That(decoded[1].TxType, Is.EqualTo(TxType.FrameTx), "the frame-tx receipt must be typed FrameTx");
        Assert.That(decoded[1].GasUsedTotal, Is.EqualTo(frameReceipt.GasUsedTotal), "frame gas used total");
        Assert.That(decoded[1].Payer, Is.EqualTo(frameReceipt.Payer), "the payer must be decoded, not skipped by realignment");
        AssertFrameReceiptsEqual(decoded[1].FrameReceipts!, frameReceipt.FrameReceipts!);

        Assert.That(decoded[2].Sender, Is.EqualTo(after.Sender), "trailing receipt sender proves realignment past the frame extension");
        Assert.That(decoded[2].GasUsedTotal, Is.EqualTo(after.GasUsedTotal), "trailing receipt gas used total");
        Assert.That(decoded[2].TxType, Is.EqualTo(TxType.Legacy));
    }

    // ReceiptsIterator (eth_getLogs) loops DecodeStructRef over stored receipts, so a frame-tx receipt must leave
    // the reader at its own end or corrupt the next one; it sits between two regulars here.
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

    private const string OldSingleNonCompactHex =
        "f90207b9020406f90200018080809476e68a8696537e4141926f3e528733af9e237d6980808082c738b9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000040000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000008000000000000000000000000000000000000000002000000000000000000000000000000080000000000000000000000000000000000010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000002000000000000000000000f83af83894942921b14f1b1c385cd7e0cc2ef7abe5598c8358e1a05fe7f977e71dba2ea1a68e21057beebb9be2ac30c6410aa38d4f3fbe41dcffd201ff808094b7705ae4c6f81b66cdb323c65f4e8133690fc099f888f84001825208f83af83894942921b14f1b1c385cd7e0cc2ef7abe5598c8358e1a05fe7f977e71dba2ea1a68e21057beebb9be2ac30c6410aa38d4f3fbe41dcffd201f84080827530f83af83894942921b14f1b1c385cd7e0cc2ef7abe5598c8358e1a0f2ee15ea639b73fa3db9b34a245bdfa015c260c598b211bf05a1ecc4b3e3b4f202c30280c0";
    private const string OldSingleCompactHex =
        "7ff8f8f8f7019476e68a8696537e4141926f3e528733af9e237d6982c738f83bf83994942921b14f1b1c385cd7e0cc2ef7abe5598c8358e1a05fe7f977e71dba2ea1a68e21057beebb9be2ac30c6410aa38d4f3fbe41dcffd2800194b7705ae4c6f81b66cdb323c65f4e8133690fc099f88af84101825208f83bf83994942921b14f1b1c385cd7e0cc2ef7abe5598c8358e1a05fe7f977e71dba2ea1a68e21057beebb9be2ac30c6410aa38d4f3fbe41dcffd28001f84180827530f83bf83994942921b14f1b1c385cd7e0cc2ef7abe5598c8358e1a0f2ee15ea639b73fa3db9b34a245bdfa015c260c598b211bf05a1ecc4b3e3b4f28002c30280c0";
    private const string OldArrayNonCompactHex =
        "f905a5f901ce01a0017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72020294475674cb523a0a2736b7f7534390288fce16982c94942921b14f1b1c385cd7e0cc2ef7abe5598c83589476e68a8696537e4141926f3e528733af9e237d69648203e8b9010000000000000000000080000000000000000000000000000000000000000000000000000000000000000000000000000200000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000800000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000000000000000000000000000000000000000000000000000f83af838940000000000000000000000000000000000000000e1a0000000000000000000000000000000000000000000000000000000000000000080ffa003783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760856572726f72b9020006f901fc018080809476e68a8696537e4141926f3e528733af9e237d6980808082c738b9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000040000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000008000000000000000000000000000000000000000002000000000000000000000000000000080000000000000000000000000000000000010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000002000000000000000000000f83af83894942921b14f1b1c385cd7e0cc2ef7abe5598c8358e1a05fe7f977e71dba2ea1a68e21057beebb9be2ac30c6410aa38d4f3fbe41dcffd201ff808094b7705ae4c6f81b66cdb323c65f4e8133690fc099f884f84001825208f83af83894942921b14f1b1c385cd7e0cc2ef7abe5598c8358e1a05fe7f977e71dba2ea1a68e21057beebb9be2ac30c6410aa38d4f3fbe41dcffd201f84080827530f83af83894942921b14f1b1c385cd7e0cc2ef7abe5598c8358e1a0f2ee15ea639b73fa3db9b34a245bdfa015c260c598b211bf05a1ecc4b3e3b4f202f901ce01a0017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f720202942d36e6c27c34ea22620e7b7c45de774599406cf394942921b14f1b1c385cd7e0cc2ef7abe5598c83589476e68a8696537e4141926f3e528733af9e237d69648207d0b9010000000000000000000080000000000000000000000000000000000000000000000000000000000000000000000000000200000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000800000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000000000000000000000000000000000000000000000000000f83af838940000000000000000000000000000000000000000e1a0000000000000000000000000000000000000000000000000000000000000000080ffa003783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760856572726f72";
    private const string OldArrayCompactHex =
        "7ff9015ef40194475674cb523a0a2736b7f7534390288fce16982c8203e8dad9940000000000000000000000000000000000000000c1008080f8f3019476e68a8696537e4141926f3e528733af9e237d6982c738f83bf83994942921b14f1b1c385cd7e0cc2ef7abe5598c8358e1a05fe7f977e71dba2ea1a68e21057beebb9be2ac30c6410aa38d4f3fbe41dcffd2800194b7705ae4c6f81b66cdb323c65f4e8133690fc099f886f84101825208f83bf83994942921b14f1b1c385cd7e0cc2ef7abe5598c8358e1a05fe7f977e71dba2ea1a68e21057beebb9be2ac30c6410aa38d4f3fbe41dcffd28001f84180827530f83bf83994942921b14f1b1c385cd7e0cc2ef7abe5598c8358e1a0f2ee15ea639b73fa3db9b34a245bdfa015c260c598b211bf05a1ecc4b3e3b4f28002f401942d36e6c27c34ea22620e7b7c45de774599406cf38207d0dad9940000000000000000000000000000000000000000c1008080";

    [Test]
    public void StorageDecode_PreTwoDimensionalScalarGasUsed_ReadsExecutionWithZeroState(
        [Values(false, true)] bool compact)
    {
        byte[] encoded = Bytes.FromHexString(compact ? OldSingleCompactHex : OldSingleNonCompactHex);
        RlpReader ctx = new(encoded);
        TxReceipt[] decoded = ReceiptArrayStorageDecoder.Instance.Decode(ref ctx, RlpBehaviors.Storage);

        Assert.That(decoded, Has.Length.EqualTo(1));
        TxReceipt receipt = decoded[0];
        Assert.That(receipt.TxType, Is.EqualTo(TxType.FrameTx));
        Assert.That(receipt.GasUsedTotal, Is.EqualTo(51_000UL));
        Assert.That(receipt.Payer, Is.EqualTo(TestItem.AddressA));
        AssertFrameReceiptsEqual(receipt.FrameReceipts!,
        [
            new TxFrameReceipt(TxFrameReceipt.StatusSuccess, 21_000, 0, [Log(0x01)]),
            new TxFrameReceipt(TxFrameReceipt.StatusFailure, 30_000, 0, [Log(0x02)]),
            new TxFrameReceipt(TxFrameReceipt.StatusSkipped, 0, 0, []),
        ]);
    }

    [Test]
    public void StorageDecode_PreTwoDimensionalFrameReceiptBetweenRegulars_DecodesAllAndStaysAligned(
        [Values(false, true)] bool compact)
    {
        byte[] encoded = Bytes.FromHexString(compact ? OldArrayCompactHex : OldArrayNonCompactHex);
        RlpReader ctx = new(encoded);
        TxReceipt[] decoded = ReceiptArrayStorageDecoder.Instance.Decode(ref ctx, RlpBehaviors.Storage);

        Assert.That(decoded, Has.Length.EqualTo(3));
        Assert.That(decoded[0].Sender, Is.EqualTo(TestItem.AddressD), "leading regular receipt sender");
        Assert.That(decoded[0].GasUsedTotal, Is.EqualTo(1000UL), "leading regular receipt gas used total");
        Assert.That(decoded[0].TxType, Is.EqualTo(TxType.Legacy));

        Assert.That(decoded[1].TxType, Is.EqualTo(TxType.FrameTx));
        AssertFrameReceiptsEqual(decoded[1].FrameReceipts!,
        [
            new TxFrameReceipt(TxFrameReceipt.StatusSuccess, 21_000, 0, [Log(0x01)]),
            new TxFrameReceipt(TxFrameReceipt.StatusFailure, 30_000, 0, [Log(0x02)]),
        ]);

        Assert.That(decoded[2].Sender, Is.EqualTo(TestItem.AddressE), "trailing regular receipt sender");
        Assert.That(decoded[2].GasUsedTotal, Is.EqualTo(2000UL), "trailing regular receipt gas used total");
        Assert.That(decoded[2].TxType, Is.EqualTo(TxType.Legacy));
    }

    [Test]
    public void StructRefIteration_OverPreTwoDimensionalArray_DoesNotThrowOrCorruptNeighbours(
        [Values(false, true)] bool compact)
    {
        byte[] encoded = Bytes.FromHexString(compact ? OldArrayCompactHex : OldArrayNonCompactHex);
        IReceiptRefDecoder refDecoder = compact
            ? new CompactReceiptStorageDecoder()
            : (IReceiptRefDecoder)new ReceiptStorageDecoder();

        ReadOnlySpan<byte> body = ReceiptArrayStorageDecoder.IsCompactEncoding(encoded) ? encoded.AsSpan(1) : encoded;
        RlpReader reader = new(body);
        int length = reader.ReadSequenceLength();
        int count = 0;
        (string Sender, ulong Gas, TxType Type)[] seen = new (string, ulong, TxType)[3];
        while (reader.Position < length)
        {
            refDecoder.DecodeStructRef(ref reader, RlpBehaviors.Storage, out TxReceiptStructRef current);
            if (count < seen.Length)
            {
                seen[count] = (current.Sender.ToString(), current.GasUsedTotal, current.TxType);
            }
            count++;
        }

        Assert.That(count, Is.EqualTo(3), "every receipt must decode, including the neighbours");
        Assert.That(seen[0], Is.EqualTo((TestItem.AddressD.ToString(), 1000UL, TxType.Legacy)));
        Assert.That(seen[2], Is.EqualTo((TestItem.AddressE.ToString(), 2000UL, TxType.Legacy)));
    }
}

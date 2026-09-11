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
/// <remarks>Non-parallelizable because the log-budget tests move <see cref="RlpLimit.InitMaxBlockGas"/>,
/// which is process-global.</remarks>
[TestFixture]
[NonParallelizable]
public class FrameTxReceiptDecoderTests
{
    // Low enough that the log-budget tests reach the ceiling with a handful of entries; the limit
    // derives as gas / GasCostOf.Log + 1.
    private const ulong EightLogBlockGas = GasCostOf.Log * 8;
    private const int EightLogBlockGasLimit = 9;

    private ulong _maxBlockGas;

    [SetUp]
    public void RecordBlockGas() => _maxBlockGas = RlpLimit.MaxBlockGas;

    [TearDown]
    public void RestoreBlockGas() => RlpLimit.InitMaxBlockGas(_maxBlockGas);

    [TestCaseSource(nameof(RoundtripCases))]
    public void Roundtrip_FrameTxReceipt_PreservesPayloadFields(TxReceipt receipt, byte expectedStatus)
    {
        ReceiptMessageDecoder decoder = new();

        byte[] encoded = decoder.EncodeNew(receipt, RlpBehaviors.None);
        RlpReader reader = new(encoded);
        TxReceipt decoded = decoder.Decode(ref reader)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.GasUsedTotal, Is.EqualTo(receipt.GasUsedTotal));
            Assert.That(decoded.Payer, Is.EqualTo(receipt.Payer));
            Assert.That(decoded.StatusCode, Is.EqualTo(expectedStatus),
                "the transaction status is absent from the wire and must be derived from the frame statuses");
        }

        AssertFrameReceiptsEqual(decoded.FrameReceipts!, receipt.FrameReceipts!);
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
        TxReceipt[] decoded = ReceiptArrayStorageDecoder.Instance.Decode(ref ctx, RlpBehaviors.Storage)!;

        Assert.That(decoded, Has.Length.EqualTo(2));

        TxReceipt decodedFrame = decoded[1];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded[0].Payer, Is.Null, "regular receipts carry no frame extension");
            Assert.That(decoded[0].FrameReceipts, Is.Null);
            Assert.That(decodedFrame.TxType, Is.EqualTo(TxType.FrameTx));
            Assert.That(decodedFrame.Payer, Is.EqualTo(frameReceipt.Payer));
        }

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
        TxReceipt[] decoded = decoder.DecodeArray(ref reader, decodeBehaviors)!;

        Assert.That(decoded, Has.Length.EqualTo(3), "every receipt must decode, including the neighbour after the frame extension");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded[0].Sender, Is.EqualTo(before.Sender), "leading receipt sender");
            Assert.That(decoded[0].GasUsedTotal, Is.EqualTo(before.GasUsedTotal), "leading receipt gas used total");
            Assert.That(decoded[0].TxType, Is.EqualTo(TxType.Legacy), "a receipt without the extension must not be labelled FrameTx");

            Assert.That(decoded[1].TxType, Is.EqualTo(TxType.FrameTx), "the frame-tx receipt must be typed FrameTx");
            Assert.That(decoded[1].GasUsedTotal, Is.EqualTo(frameReceipt.GasUsedTotal), "frame gas used total");
            Assert.That(decoded[1].Payer, Is.EqualTo(frameReceipt.Payer), "the payer must be decoded, not skipped by realignment");

            Assert.That(decoded[2].Sender, Is.EqualTo(after.Sender), "trailing receipt sender proves realignment past the frame extension");
            Assert.That(decoded[2].GasUsedTotal, Is.EqualTo(after.GasUsedTotal), "trailing receipt gas used total");
            Assert.That(decoded[2].TxType, Is.EqualTo(TxType.Legacy));
        }

        AssertFrameReceiptsEqual(decoded[1].FrameReceipts!, frameReceipt.FrameReceipts!);
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded[0].Sender, Is.EqualTo(before.Sender!.ToString()), "leading receipt sender");
            Assert.That(decoded[0].Gas, Is.EqualTo(before.GasUsedTotal), "leading receipt gas used total");
            Assert.That(decoded[0].Type, Is.EqualTo(TxType.Legacy), "a receipt without the extension must not be labelled FrameTx");

            Assert.That(decoded[1].Status, Is.EqualTo(frameReceipt.StatusCode), "frame status");
            Assert.That(decoded[1].Gas, Is.EqualTo(frameReceipt.GasUsedTotal), "frame gas used total");
            Assert.That(decoded[1].Sender, Is.EqualTo(frameReceipt.Sender!.ToString()), "frame sender");
            // TxType here is decoder-assigned and only observed by callers that skip recovery; on
            // eth_getLogs recovery overwrites it from the matching transaction.
            Assert.That(decoded[1].Type, Is.EqualTo(TxType.FrameTx), "the frame-tx receipt must be typed FrameTx");

            // The trailing receipt decodes intact only if the reader advanced past the frame extension.
            Assert.That(decoded[2].Sender, Is.EqualTo(after.Sender!.ToString()), "trailing receipt sender");
            Assert.That(decoded[2].Gas, Is.EqualTo(after.GasUsedTotal), "trailing receipt gas used total");
            Assert.That(decoded[2].Type, Is.EqualTo(TxType.Legacy));
        }

        AssertLogsEqual(decoded[1].Logs, frameReceipt.Logs!);
    }

    /// <summary>The on-disk storage encoding persists each log twice — in the top-level union sequence and again
    /// inside its frame receipt — so a frame-tx receipt is a full logs copy larger than the wire form, the trade-off
    /// that lets the zero-alloc <c>DecodeStructRef</c> path slice the top-level logs without rebuilding them from the
    /// frames. The literal length pins that on-disk size so a second duplication cannot land unnoticed.</summary>
    [Test]
    public void StorageEncoding_MultiFrameReceiptWithLogs_StoresLogsTwiceAtPinnedSizeAndRoundtrips(
        [Values(true, false)] bool compact)
    {
        TxFrameReceipt[] frameReceipts =
        [
            new TxFrameReceipt(TxFrameReceipt.StatusSuccess, 21_000, 5_000, [Log(0x01), Log(0x02)]),
            new TxFrameReceipt(TxFrameReceipt.StatusFailure, 30_000, 0, [Log(0x03)]),
            new TxFrameReceipt(TxFrameReceipt.StatusSkipped, 0, 0, []),
        ];
        LogEntry[] unionLogs = frameReceipts.SelectMany(static frame => frame.Logs).ToArray();
        TxReceipt receipt = CreateStorageFrameReceipt(unionLogs, frameReceipts);

        RlpDecoder<TxReceipt> decoder = compact ? new CompactReceiptStorageDecoder() : new ReceiptStorageDecoder();
        RlpBehaviors behaviors = RlpBehaviors.Storage | RlpBehaviors.Eip658Receipts;
        byte[] encoded = decoder.EncodeAsBytes(receipt, behaviors);

        int expectedLength = compact ? 435 : 701;
        Assert.That(encoded.Length, Is.EqualTo(expectedLength),
            "the stored size counts every log twice; a change here means a copy was added or removed");

        RlpReader reader = new(encoded);
        TxReceipt decoded = decoder.Decode(ref reader, RlpBehaviors.Storage)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.TxType, Is.EqualTo(TxType.FrameTx));
            Assert.That(decoded.Payer, Is.EqualTo(receipt.Payer));
            Assert.That(decoded.GasUsedTotal, Is.EqualTo(receipt.GasUsedTotal));
            Assert.That(decoded.StatusCode, Is.EqualTo(receipt.StatusCode));
        }

        AssertLogsEqual(decoded.Logs!, unionLogs, "the top-level union copy of the logs must survive the round-trip");
        AssertFrameReceiptsEqual(decoded.FrameReceipts!, frameReceipts);
        AssertLogsEqual(decoded.FrameReceipts!.SelectMany(static frame => frame.Logs).ToArray(), unionLogs,
            "the per-frame copy holds the same logs as the top-level union, the duplication this size pins");
    }

    /// <summary>A pre-fork receipt's tx-hash mark and error sit in the same trailing region the frame extension
    /// was appended to. This pins that a non-frame receipt already on disk still reads back with both fields, and
    /// that an array of them stays aligned, under the storage behaviours the read paths use.</summary>
    [Test]
    public void StorageDecode_NonFrameReceiptsInTheOnDiskShape_KeepTxHashAndErrorAndStayAligned(
        [Values(RlpBehaviors.Storage, RlpBehaviors.Storage | RlpBehaviors.AllowExtraBytes)] RlpBehaviors decodeBehaviors)
    {
        byte[] encoded = Bytes.FromHexString(NonFrameArrayNonCompactHex);

        ReceiptStorageDecoder decoder = new();
        RlpReader reader = new(encoded);
        TxReceipt[] decoded = decoder.DecodeArray(ref reader, decodeBehaviors)!;

        Assert.That(decoded, Has.Length.EqualTo(2));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded[0].TxHash, Is.EqualTo(TestItem.KeccakA), "leading receipt tx hash");
            Assert.That(decoded[0].Error, Is.EqualTo("error"), "leading receipt error");
            Assert.That(decoded[0].Sender, Is.EqualTo(TestItem.AddressD), "leading receipt sender");
            Assert.That(decoded[0].TxType, Is.EqualTo(TxType.Legacy));

            Assert.That(decoded[1].TxHash, Is.EqualTo(TestItem.KeccakB), "trailing receipt tx hash");
            Assert.That(decoded[1].Error, Is.EqualTo("error"), "trailing receipt error");
            Assert.That(decoded[1].Sender, Is.EqualTo(TestItem.AddressE),
                "the trailing receipt decodes intact only if the reader realigned past the leading one");
            Assert.That(decoded[1].TxType, Is.EqualTo(TxType.Legacy));
        }
    }

    /// <summary>The same bytes over the struct-ref path ReceiptsIterator uses. It reads the same two trailing
    /// fields as the object path, so a receipt cannot carry a tx hash and an error through one and not the other.</summary>
    [Test]
    public void StructRefDecode_NonFrameReceiptsInTheOnDiskShape_KeepTxHashAndErrorAndStayAligned(
        [Values(RlpBehaviors.Storage, RlpBehaviors.Storage | RlpBehaviors.AllowExtraBytes)] RlpBehaviors decodeBehaviors)
    {
        byte[] encoded = Bytes.FromHexString(NonFrameArrayNonCompactHex);

        IReceiptRefDecoder refDecoder = new ReceiptStorageDecoder();
        RlpReader reader = new(encoded);
        int length = reader.ReadSequenceLength() + reader.Position;
        (string TxHash, string? Error, string Sender)[] decoded = new (string, string?, string)[2];
        int count = 0;
        while (reader.Position < length)
        {
            refDecoder.DecodeStructRef(ref reader, decodeBehaviors, out TxReceiptStructRef current);
            if (count < decoded.Length)
            {
                decoded[count] = (current.TxHash.ToString(), current.Error, current.Sender.ToString());
            }
            count++;
        }

        Assert.That(count, Is.EqualTo(2));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded[0].TxHash, Is.EqualTo(TestItem.KeccakA.ToString()), "leading receipt tx hash");
            Assert.That(decoded[0].Error, Is.EqualTo("error"), "leading receipt error");

            Assert.That(decoded[1].TxHash, Is.EqualTo(TestItem.KeccakB.ToString()), "trailing receipt tx hash");
            Assert.That(decoded[1].Error, Is.EqualTo("error"), "trailing receipt error");
            Assert.That(decoded[1].Sender, Is.EqualTo(TestItem.AddressE.ToString()),
                "the trailing receipt decodes intact only if the reader realigned past the leading one");
        }
    }

    /// <summary>The stored write path is null-tolerant on the payer while every read path is not, so a payer-less
    /// frame receipt used to persist and then fail every later read of its block. Refused at the encoder instead.</summary>
    [Test]
    public void Encode_FrameTxReceiptWithoutPayer_IsRefusedRatherThanStoredUnreadable(
        [Values(Format.NonCompactStorage, Format.CompactStorage, Format.Message)] Format format)
    {
        TxReceipt payerless = CreateStorageFrameReceipt(
            [Log(0x01)], new TxFrameReceipt(TxFrameReceipt.StatusSuccess, 21_000, 5_000, [Log(0x01)]));
        payerless.Payer = null;

        Assert.That(() => EncodeStored(payerless, format), Throws.InstanceOf<RlpException>());
    }

    // The counterpart: the refusal must not stand between a payer-carrying frame receipt and the round trip.
    [Test]
    public void Encode_FrameTxReceiptWithPayer_RoundtripsThePayer(
        [Values(Format.NonCompactStorage, Format.CompactStorage, Format.Message)] Format format)
    {
        TxReceipt receipt = CreateStorageFrameReceipt(
            [Log(0x01)], new TxFrameReceipt(TxFrameReceipt.StatusSuccess, 21_000, 5_000, [Log(0x01)]));

        Assert.That(DecodeStored(EncodeStored(receipt, format), format).Payer, Is.EqualTo(receipt.Payer));
    }

    /// <summary>A frame transaction always executes at least one frame, so the frames-less shape is the other
    /// receipt that still encodes but no longer decodes. Refused at the encoder, as the payer is.</summary>
    [Test]
    public void Encode_FrameTxReceiptWithoutFrameReceipts_IsRefusedRatherThanStoredUnreadable(
        [Values(Format.NonCompactStorage, Format.CompactStorage, Format.Message)] Format format,
        [Values(false, true)] bool empty)
    {
        TxReceipt framesless = CreateStorageFrameReceipt([Log(0x01)]);
        framesless.FrameReceipts = empty ? [] : null;

        Assert.That(() => EncodeStored(framesless, format), Throws.InstanceOf<RlpException>());
    }

    /// <summary>Both halves of the wire pair refuse it, not just the length half. Every message encoder happens to
    /// call <c>GetPayloadLength</c> first today, so the throw lands there — a call order, not a codec invariant.</summary>
    [Test]
    public void EncodePayload_FrameTxReceiptWithoutPayer_IsRefusedWithoutMeasuringItFirst()
    {
        TxReceipt payerless = CreateStorageFrameReceipt(
            [Log(0x01)], new TxFrameReceipt(TxFrameReceipt.StatusSuccess, 21_000, 5_000, [Log(0x01)]));
        payerless.Payer = null;

        Assert.That(() =>
        {
            RlpWriter writer = new(new byte[1024]);
            FrameReceiptRlp.EncodePayload(ref writer, payerless, framesLength: 0);
        }, Throws.InstanceOf<RlpException>());
    }

    /// <summary>The bounds the payload decoder holds are enforced on the write paths, not the stored read path:
    /// a stored row that fails to parse takes its whole block's receipt array with it, and no encoder is left
    /// that could reproduce the bytes to migrate it.</summary>
    [TestCase(Format.NonCompactStorage, Eip8141Constants.MaxFrames, false)]
    [TestCase(Format.CompactStorage, Eip8141Constants.MaxFrames, false)]
    [TestCase(Format.Message, Eip8141Constants.MaxFrames, false)]
    [TestCase(Format.NonCompactStorage, Eip8141Constants.MaxFrames + 1, true)]
    [TestCase(Format.CompactStorage, Eip8141Constants.MaxFrames + 1, true)]
    [TestCase(Format.Message, Eip8141Constants.MaxFrames + 1, true)]
    public void Encode_FrameCountHoldsTheWireBound(Format format, int frameCount, bool rejected)
    {
        TxFrameReceipt[] frames = new TxFrameReceipt[frameCount];
        Array.Fill(frames, new TxFrameReceipt(TxFrameReceipt.StatusSuccess, 21_000, 0, []));
        TxReceipt receipt = CreateStorageFrameReceipt([], frames);

        if (rejected)
        {
            Assert.That(() => EncodeStored(receipt, format), Throws.InstanceOf<RlpException>());
        }
        else
        {
            Assert.That(DecodeStored(EncodeStored(receipt, format), format).FrameReceipts, Has.Length.EqualTo(frameCount));
        }
    }

    // Same reasoning for the status byte: AggregateStatus folds anything but success to failure while RPC surfaces
    // the raw byte, so an undefined status reads differently at the two ends of the same receipt.
    [TestCase(Format.NonCompactStorage, TxFrameReceipt.StatusFailure, false)]
    [TestCase(Format.NonCompactStorage, TxFrameReceipt.StatusSuccess, false)]
    [TestCase(Format.NonCompactStorage, TxFrameReceipt.StatusSkipped, false)]
    [TestCase(Format.NonCompactStorage, (byte)3, true)]
    [TestCase(Format.NonCompactStorage, byte.MaxValue, true)]
    [TestCase(Format.CompactStorage, TxFrameReceipt.StatusSkipped, false)]
    [TestCase(Format.CompactStorage, (byte)3, true)]
    [TestCase(Format.CompactStorage, byte.MaxValue, true)]
    [TestCase(Format.Message, TxFrameReceipt.StatusSkipped, false)]
    [TestCase(Format.Message, (byte)3, true)]
    [TestCase(Format.Message, byte.MaxValue, true)]
    public void Encode_FrameStatusOutsideThePayloadValues_IsRefused(Format format, byte status, bool rejected)
    {
        TxReceipt receipt = CreateStorageFrameReceipt([], new TxFrameReceipt(status, 21_000, 0, []));

        if (rejected)
        {
            Assert.That(() => EncodeStored(receipt, format), Throws.InstanceOf<RlpException>());
        }
        else
        {
            Assert.That(DecodeStored(EncodeStored(receipt, format), format).FrameReceipts![0].Status, Is.EqualTo(status));
        }
    }

    /// <summary>The third of the payload decoder's bounds: it budgets the whole receipt rather than each frame,
    /// so two frames that are each within it can still sum past it.</summary>
    [Test]
    public void Encode_FrameLogsSummingOverTheReceiptBound_IsRefused(
        [Values(Format.NonCompactStorage, Format.CompactStorage, Format.Message)] Format format,
        [Values(false, true)] bool over)
    {
        RlpLimit.InitMaxBlockGas(EightLogBlockGas);

        // One shared entry repeated: the guard counts logs and never reaches their contents.
        LogEntry[] half = new LogEntry[over ? RlpLimit.ReceiptLogs.Limit / 2 + 1 : 1];
        Array.Fill(half, Log(0x01));
        TxReceipt receipt = CreateStorageFrameReceipt([],
            new TxFrameReceipt(TxFrameReceipt.StatusSuccess, 21_000, 0, half),
            new TxFrameReceipt(TxFrameReceipt.StatusSuccess, 21_000, 0, half));

        if (over)
        {
            Assert.That(() => EncodeStored(receipt, format), Throws.InstanceOf<RlpException>());
        }
        else
        {
            Assert.That(DecodeStored(EncodeStored(receipt, format), format).FrameReceipts, Has.Length.EqualTo(2));
        }
    }

    /// <summary>Rows an earlier build of this branch already wrote — <c>debug_insertReceipts</c> bounded neither
    /// the payer nor the frame status until the guards above. They must still read back: the receipt array of a
    /// block decodes in one pass, so refusing one row makes every receipt in its block unreadable.</summary>
    [TestCase(Format.NonCompactStorage, PayerlessNonCompactHex)]
    [TestCase(Format.CompactStorage, PayerlessCompactHex)]
    public void StorageDecode_PayerlessRowFromAnEarlierBuild_ReadsBackAndOnlyTheWireEncoderRefusesIt(Format format, string hex)
    {
        TxReceipt decoded = DecodeStored(Bytes.FromHexString(hex), format);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.TxType, Is.EqualTo(TxType.FrameTx));
            Assert.That(decoded.Payer, Is.Null, "the row parses; the missing payer is surfaced rather than thrown on");
            Assert.That(decoded.FrameReceipts, Has.Length.EqualTo(1));
        }

        Assert.That(() => new ReceiptMessageDecoder().EncodeNew(decoded, RlpBehaviors.None), Throws.InstanceOf<RlpException>(),
            "a payer-less receipt is still not servable, so the refusal belongs to the wire encoder");
    }

    [TestCase(Format.NonCompactStorage, UndefinedStatusNonCompactHex)]
    [TestCase(Format.CompactStorage, UndefinedStatusCompactHex)]
    public void StorageDecode_UndefinedFrameStatusFromAnEarlierBuild_ReadsBack(Format format, string hex)
    {
        TxReceipt decoded = DecodeStored(Bytes.FromHexString(hex), format);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.Payer, Is.EqualTo(TestItem.AddressA));
            Assert.That(decoded.FrameReceipts![0].Status, Is.EqualTo((byte)3), "the raw status is surfaced, not rejected");
        }
    }

    /// <summary>The receipt codecs a frame receipt can be written through.</summary>
    public enum Format
    {
        NonCompactStorage,
        CompactStorage,
        Message,
    }

    private static RlpDecoder<TxReceipt> DecoderFor(Format format) => format switch
    {
        Format.NonCompactStorage => new ReceiptStorageDecoder(),
        Format.CompactStorage => new CompactReceiptStorageDecoder(),
        _ => new ReceiptMessageDecoder(),
    };

    private static byte[] EncodeStored(TxReceipt receipt, Format format) => format == Format.Message
        ? new ReceiptMessageDecoder().EncodeNew(receipt, RlpBehaviors.None)
        : DecoderFor(format).EncodeAsBytes(receipt, RlpBehaviors.Storage | RlpBehaviors.Eip658Receipts);

    private static TxReceipt DecodeStored(byte[] encoded, Format format)
    {
        RlpReader reader = new(encoded);
        return DecoderFor(format).Decode(ref reader, format == Format.Message ? RlpBehaviors.None : RlpBehaviors.Storage)!;
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
            using (Assert.EnterMultipleScope())
            {
                Assert.That(actual[i].Status, Is.EqualTo(expected[i].Status), $"frame receipt {i} status");
                Assert.That(actual[i].ExecutionGasUsed, Is.EqualTo(expected[i].ExecutionGasUsed), $"frame receipt {i} execution gas used");
                Assert.That(actual[i].StateGasUsed, Is.EqualTo(expected[i].StateGasUsed), $"frame receipt {i} state gas used");
            }

            // Outside the scope above: its own length guard must stop before the per-log field reads.
            AssertLogsEqual(actual[i].Logs, expected[i].Logs);
        }
    }

    // LogEntry has no value equality, so logs are compared field by field.
    private static void AssertLogsEqual(LogEntry[] actual, LogEntry[] expected, string? message = null)
    {
        Assert.That(actual.Length, Is.EqualTo(expected.Length), message);
        using (Assert.EnterMultipleScope())
        {
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.That(actual[i].Address, Is.EqualTo(expected[i].Address), $"log {i} address");
                Assert.That(actual[i].Data.ToArray(), Is.EqualTo(expected[i].Data.ToArray()), $"log {i} data");
                Assert.That(actual[i].Topics, Is.EqualTo(expected[i].Topics), $"log {i} topics");
            }
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

    // Two pre-fork receipts as the non-compact storage codec has always written them: logs, the 0xff tx-hash
    // mark, the hash, then the error string. Captured before the frame extension was appended after it.
    private const string NonFrameArrayNonCompactHex = "f903a2f901ce01a0017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72020294475674cb523a0a2736b7f7534390288fce16982c94942921b14f1b1c385cd7e0cc2ef7abe5598c83589476e68a8696537e4141926f3e528733af9e237d69648203e8b9010000000000000000000080000000000000000000000000000000000000000000000000000000000000000000000000000200000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000800000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000000000000000000000000000000000000000000000000000f83af838940000000000000000000000000000000000000000e1a0000000000000000000000000000000000000000000000000000000000000000080ffa003783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760856572726f72f901ce01a0017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f720202942d36e6c27c34ea22620e7b7c45de774599406cf394942921b14f1b1c385cd7e0cc2ef7abe5598c83589476e68a8696537e4141926f3e528733af9e237d69648203e8b9010000000000000000000080000000000000000000000000000000000000000000000000000000000000000000000000000200000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000800000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000000000000000000000000000000000000000000000000000f83af838940000000000000000000000000000000000000000e1a0000000000000000000000000000000000000000000000000000000000000000080ffa01f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111856572726f72";

    // Rows an earlier build of this branch wrote through debug_insertReceipts, both encoders' output for a
    // single-frame receipt: one with the payer omitted, one with a frame status of 3.
    private const string PayerlessNonCompactHex = "b901ae06f901aa018080809476e68a8696537e4141926f3e528733af9e237d69808080826590b9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000040000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000008000000000000000000000000000000000000000002000000000000000000000000000000080000000000000000000000000000000000010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000002000000000000000000000f83af83894942921b14f1b1c385cd7e0cc2ef7abe5598c8358e1a05fe7f977e71dba2ea1a68e21057beebb9be2ac30c6410aa38d4f3fbe41dcffd201ff808080f846f84401c6825208821388f83af83894942921b14f1b1c385cd7e0cc2ef7abe5598c8358e1a05fe7f977e71dba2ea1a68e21057beebb9be2ac30c6410aa38d4f3fbe41dcffd201";
    private const string PayerlessCompactHex = "f8a0019476e68a8696537e4141926f3e528733af9e237d69826590f83bf83994942921b14f1b1c385cd7e0cc2ef7abe5598c8358e1a05fe7f977e71dba2ea1a68e21057beebb9be2ac30c6410aa38d4f3fbe41dcffd2800180f847f84501c6825208821388f83bf83994942921b14f1b1c385cd7e0cc2ef7abe5598c8358e1a05fe7f977e71dba2ea1a68e21057beebb9be2ac30c6410aa38d4f3fbe41dcffd28001";
    private const string UndefinedStatusNonCompactHex = "b901c206f901be018080809476e68a8696537e4141926f3e528733af9e237d69808080826590b9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000040000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000008000000000000000000000000000000000000000002000000000000000000000000000000080000000000000000000000000000000000010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000002000000000000000000000f83af83894942921b14f1b1c385cd7e0cc2ef7abe5598c8358e1a05fe7f977e71dba2ea1a68e21057beebb9be2ac30c6410aa38d4f3fbe41dcffd201ff808094b7705ae4c6f81b66cdb323c65f4e8133690fc099f846f84403c6825208821388f83af83894942921b14f1b1c385cd7e0cc2ef7abe5598c8358e1a05fe7f977e71dba2ea1a68e21057beebb9be2ac30c6410aa38d4f3fbe41dcffd201";
    private const string UndefinedStatusCompactHex = "f8b4019476e68a8696537e4141926f3e528733af9e237d69826590f83bf83994942921b14f1b1c385cd7e0cc2ef7abe5598c8358e1a05fe7f977e71dba2ea1a68e21057beebb9be2ac30c6410aa38d4f3fbe41dcffd2800194b7705ae4c6f81b66cdb323c65f4e8133690fc099f847f84503c6825208821388f83bf83994942921b14f1b1c385cd7e0cc2ef7abe5598c8358e1a05fe7f977e71dba2ea1a68e21057beebb9be2ac30c6410aa38d4f3fbe41dcffd28001";

    [Test]
    public void StorageDecode_PreTwoDimensionalScalarGasUsed_ReadsExecutionWithZeroState(
        [Values(false, true)] bool compact)
    {
        byte[] encoded = Bytes.FromHexString(compact ? OldSingleCompactHex : OldSingleNonCompactHex);
        RlpReader ctx = new(encoded);
        TxReceipt[] decoded = ReceiptArrayStorageDecoder.Instance.Decode(ref ctx, RlpBehaviors.Storage)!;

        Assert.That(decoded, Has.Length.EqualTo(1));
        TxReceipt receipt = decoded[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.TxType, Is.EqualTo(TxType.FrameTx));
            Assert.That(receipt.GasUsedTotal, Is.EqualTo(51_000UL));
            Assert.That(receipt.Payer, Is.EqualTo(TestItem.AddressA));
        }

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
        TxReceipt[] decoded = ReceiptArrayStorageDecoder.Instance.Decode(ref ctx, RlpBehaviors.Storage)!;

        Assert.That(decoded, Has.Length.EqualTo(3));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded[0].Sender, Is.EqualTo(TestItem.AddressD), "leading regular receipt sender");
            Assert.That(decoded[0].GasUsedTotal, Is.EqualTo(1000UL), "leading regular receipt gas used total");
            Assert.That(decoded[0].TxType, Is.EqualTo(TxType.Legacy));

            Assert.That(decoded[1].TxType, Is.EqualTo(TxType.FrameTx));

            Assert.That(decoded[2].Sender, Is.EqualTo(TestItem.AddressE), "trailing regular receipt sender");
            Assert.That(decoded[2].GasUsedTotal, Is.EqualTo(2000UL), "trailing regular receipt gas used total");
            Assert.That(decoded[2].TxType, Is.EqualTo(TxType.Legacy));
        }

        AssertFrameReceiptsEqual(decoded[1].FrameReceipts!,
        [
            new TxFrameReceipt(TxFrameReceipt.StatusSuccess, 21_000, 0, [Log(0x01)]),
            new TxFrameReceipt(TxFrameReceipt.StatusFailure, 30_000, 0, [Log(0x02)]),
        ]);
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(seen[0], Is.EqualTo((TestItem.AddressD.ToString(), 1000UL, TxType.Legacy)));
            Assert.That(seen[1], Is.EqualTo((TestItem.AddressC.ToString(), 51_000UL, TxType.FrameTx)));
            Assert.That(seen[2], Is.EqualTo((TestItem.AddressE.ToString(), 2000UL, TxType.Legacy)));
        }
    }

    // The payload defines only failure, success and skipped. An out-of-range byte round-trips, so the decoder is
    // the only place it can be caught before AggregateStatus folds it to failure and RPC surfaces it raw.
    [TestCase(TxFrameReceipt.StatusFailure, false)]
    [TestCase(TxFrameReceipt.StatusSuccess, false)]
    [TestCase(TxFrameReceipt.StatusSkipped, false)]
    [TestCase((byte)3, true)]
    [TestCase(byte.MaxValue, true)]
    public void MessageDecode_FrameStatusOutsideThePayloadValues_Throws(byte status, bool rejected)
    {
        TxReceipt receipt = CreateReceipt(new TxFrameReceipt(status, 21_000, 0, []));

        if (rejected)
        {
            Assert.That(() => DecodeMessage(receipt), Throws.InstanceOf<RlpException>());
        }
        else
        {
            Assert.That(DecodeMessage(receipt).FrameReceipts![0].Status, Is.EqualTo(status));
        }
    }

    // The log ceiling is derived from a whole transaction's gas, so the frames share one budget; spent per frame
    // it would admit MaxFrames times the emissions it stands for.
    [TestCase(0, false, TestName = "MessageDecode_FrameLogsAtTheReceiptBudget_IsAccepted")]
    [TestCase(1, true, TestName = "MessageDecode_FrameLogsOverTheReceiptBudget_Throws")]
    public void MessageDecode_FrameLogBudgetIsSpentPerReceiptNotPerFrame(int excess, bool rejected)
    {
        RlpLimit.InitMaxBlockGas(EightLogBlockGas);

        int maxReceiptLogs = RlpLimit.ReceiptLogs.Limit;
        int firstFrameLogs = maxReceiptLogs / 2;
        int secondFrameLogs = maxReceiptLogs - firstFrameLogs + excess;
        // Each frame stays under the ceiling on its own, so only their sum can trip the guard.
        TxReceipt receipt = CreateReceipt(
            new TxFrameReceipt(TxFrameReceipt.StatusSuccess, 21_000, 0, RepeatedLogs(firstFrameLogs)),
            new TxFrameReceipt(TxFrameReceipt.StatusSuccess, 21_000, 0, RepeatedLogs(secondFrameLogs)));

        if (rejected)
        {
            Assert.That(() => DecodeMessage(receipt), Throws.InstanceOf<RlpException>());
        }
        else
        {
            Assert.That(DecodeMessage(receipt).Logs, Has.Length.EqualTo(maxReceiptLogs));
        }
    }

    /// <summary>Frame receipts return from the message decoders before <see cref="LogEntryDecoder.DecodeLogs"/>,
    /// so only this pins them to the same gas-derived ceiling every other receipt kind reads under.</summary>
    [Test]
    public void MessageDecode_FrameLogCount_IsBoundedByTheConfiguredBlockGas()
    {
        int overTheLimit = EightLogBlockGasLimit + 1;
        byte[] encoded = EncodeMessage(CreateReceipt(
            new TxFrameReceipt(TxFrameReceipt.StatusSuccess, 21_000, 0, RepeatedLogs(overTheLimit))));
        Assert.That(DecodeMessage(encoded).Logs, Has.Length.EqualTo(overTheLimit));

        RlpLimit.InitMaxBlockGas(EightLogBlockGas);

        Assert.That(() => DecodeMessage(encoded), Throws.InstanceOf<RlpException>());
    }

    private static TxReceipt DecodeMessage(TxReceipt receipt) => DecodeMessage(EncodeMessage(receipt));

    private static byte[] EncodeMessage(TxReceipt receipt) => new ReceiptMessageDecoder().EncodeNew(receipt, RlpBehaviors.None);

    private static TxReceipt DecodeMessage(byte[] encoded)
    {
        RlpReader reader = new(encoded);
        return new ReceiptMessageDecoder().Decode(ref reader)!;
    }

    // One shared instance: only the count matters here, and encoding never mutates a log entry.
    private static LogEntry[] RepeatedLogs(int count)
    {
        LogEntry[] logs = new LogEntry[count];
        Array.Fill(logs, new LogEntry(TestItem.AddressB, [], []));
        return logs;
    }
}

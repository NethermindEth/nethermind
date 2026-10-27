// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.Serialization.Rlp;

/// <summary>
/// The RLP codec of the EIP-8141 per-frame receipts, shared by the consensus and eth/69+ payload
/// <c>[cumulative_gas_used, payer, [[status, gas_used, logs], ...]]</c> and by the trailing
/// <c>[payer, [frame_receipt, ...]]</c> extension both storage formats append.
/// https://eips.ethereum.org/EIPS/eip-8141
/// </summary>
/// <remarks>The storage formats differ only in their log codec, so they pass their own, and they decode
/// leniently where the wire is strict: the shapes the wire refuses are refused on the write paths instead, so
/// a row an earlier build already persisted still reads back. Every format writes through the same length and
/// encode paths, so a change to the frame layout cannot reach one of them alone.</remarks>
public static class FrameReceiptRlp
{
    /// <summary>The most logs a receipt may carry - the same ceiling every other receipt kind reads under.</summary>
    /// <remarks>Read per use rather than captured in a field: the value follows the configured block gas,
    /// which is set after this type's initializer may have run.</remarks>
    private static RlpLimit LogsRlpLimit => RlpLimit.ReceiptLogs;

    private static readonly RlpLimit FrameReceiptsRlpLimit = RlpLimit.For<TxReceipt>(Eip8141Constants.MaxFrames, nameof(TxReceipt.FrameReceipts));

    /// <summary>Populates the frames list and returns its length, its own header included.</summary>
    private static int PopulateFramesLength(TxFrameReceipt[] frameReceipts, IRlpDecoder<LogEntry?> logDecoder, ref LengthStack lengths)
    {
        int framesLength = 0;
        for (int i = frameReceipts.Length - 1; i >= 0; i--)
        {
            TxFrameReceipt frameReceipt = frameReceipts[i];
            int frameLength = Rlp.LengthOf((ulong)frameReceipt.Status)
                              + Rlp.LengthOfSequence(GetGasUsedLength(frameReceipt))
                              + logDecoder.PopulateSequenceAndPop(frameReceipt.Logs, ref lengths);
            lengths.Push(frameLength);
            framesLength += Rlp.LengthOfSequence(frameLength);
        }

        lengths.Push(framesLength);
        return Rlp.LengthOfSequence(framesLength);
    }

    /// <summary>Decodes the frames list of a stored receipt's <c>[payer, [frame_receipt, ...]]</c> extension.</summary>
    /// <remarks>Lenient by design: the data is locally written, and it may predate the 2D <c>gas_used</c>.</remarks>
    internal static TxFrameReceipt[] DecodeStoredFrames(ref RlpReader reader, IRlpDecoder<LogEntry?> logDecoder)
    {
        int framesEnd = reader.ReadSequenceLength() + reader.Position;
        using ArrayPoolListRef<TxFrameReceipt> frameReceipts = new(Eip8141Constants.MaxFrames);
        while (reader.Position < framesEnd)
        {
            int frameEnd = reader.ReadSequenceLength() + reader.Position;
            byte status = reader.DecodeByte();
            DecodeStoredGasUsed(ref reader, out ulong executionGasUsed, out ulong stateGasUsed);

            int logsEnd = reader.ReadSequenceLength() + reader.Position;
            using ArrayPoolListRef<LogEntry> frameLogs = new(4);
            while (reader.Position < logsEnd)
            {
                frameLogs.Add(logDecoder.DecodeGuardNotNull(ref reader, RlpBehaviors.AllowExtraBytes));
            }

            frameReceipts.Add(new TxFrameReceipt(status, executionGasUsed, stateGasUsed, frameLogs.ToArray()));
            reader.Check(frameEnd);
        }

        return frameReceipts.ToArray();
    }

    /// <summary>Populates a stored receipt's trailing <c>[payer, frames]</c> extension and returns its content length.</summary>
    /// <exception cref="RlpException">The receipt is one no read path would take back.</exception>
    internal static int PopulateStoredExtensionLength(TxReceipt receipt, IRlpDecoder<LogEntry?> logDecoder, ref LengthStack lengths)
    {
        TxFrameReceipt[] frameReceipts = GuardEncodable(receipt);
        return Rlp.LengthOf(receipt.Payer) + PopulateFramesLength(frameReceipts, logDecoder, ref lengths);
    }

    /// <summary>Writes a stored receipt's trailing <c>[payer, frames]</c> extension.</summary>
    /// <exception cref="RlpException">The receipt is one no read path would take back.</exception>
    internal static void EncodeStoredExtension<TWriter>(ref TWriter writer, TxReceipt receipt, IRlpDecoder<LogEntry?> logDecoder, ref LengthStack lengths)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        TxFrameReceipt[] frameReceipts = GuardEncodable(receipt);
        writer.Encode(receipt.Payer);
        EncodeFrames(ref writer, frameReceipts, logDecoder, ref lengths);
    }

    /// <summary>Rejects a receipt whose shape a read path would refuse, rather than writing it.</summary>
    /// <remarks>The bounds are held here rather than in <see cref="DecodeStoredFrames"/> because a stored row
    /// that fails to parse takes its whole block's receipt array with it, and no encoder could reproduce the
    /// bytes to migrate it. Refusing on the way in keeps a row already on disk readable.</remarks>
    /// <returns>The receipt's frame receipts, once they are known to be encodable.</returns>
    /// <exception cref="RlpException">The receipt is one no read path would take back.</exception>
    private static TxFrameReceipt[] GuardEncodable(TxReceipt receipt)
    {
        if (receipt.Payer is null)
        {
            ThrowMissingPayer();
        }

        TxFrameReceipt[] frameReceipts = receipt.FrameReceipts ?? [];
        if (frameReceipts.Length == 0)
        {
            ThrowEmptyFrameReceipts();
        }

        if (frameReceipts.Length > Eip8141Constants.MaxFrames)
        {
            ThrowTooManyFrames(frameReceipts.Length);
        }

        int totalLogs = 0;
        for (int i = 0; i < frameReceipts.Length; i++)
        {
            GuardFrameStatus(frameReceipts[i].Status);
            totalLogs += frameReceipts[i].Logs.Length;
        }

        int logsLimit = LogsRlpLimit.Limit;
        if (totalLogs > logsLimit)
        {
            ThrowTooManyFrameLogs(totalLogs, logsLimit);
        }

        return frameReceipts;
    }

    private static void GuardFrameStatus(byte status)
    {
        if (status is not (TxFrameReceipt.StatusFailure or TxFrameReceipt.StatusSuccess or TxFrameReceipt.StatusSkipped))
        {
            ThrowUnknownFrameStatus(status);
        }
    }

    /// <summary>Populates the payload fields <c>[cumulative_gas_used, payer, frames]</c> and returns their content length,
    /// the sequence enclosing them excluded.</summary>
    /// <param name="receipt">The frame-transaction receipt to measure.</param>
    /// <param name="lengths">Receives the lengths <see cref="EncodePayload{TWriter}"/> pops.</param>
    /// <exception cref="RlpException">The receipt is one no read path would take back.</exception>
    public static int PopulatePayloadLength(TxReceipt receipt, ref LengthStack lengths) =>
        Rlp.LengthOf(receipt.GasUsedTotal)
        + Rlp.LengthOf(receipt.Payer)
        + PopulateFramesLength(GuardEncodable(receipt), LogEntryDecoder.Instance, ref lengths);

    /// <summary>Writes the payload fields populated by <see cref="PopulatePayloadLength"/>; the caller owns the sequence enclosing them.</summary>
    /// <exception cref="RlpException">The receipt is one no read path would take back.</exception>
    public static void EncodePayload<TWriter>(ref TWriter writer, TxReceipt receipt, ref LengthStack lengths)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        TxFrameReceipt[] frameReceipts = GuardEncodable(receipt);
        writer.Encode(receipt.GasUsedTotal);
        writer.Encode(receipt.Payer);
        EncodeFrames(ref writer, frameReceipts, LogEntryDecoder.Instance, ref lengths);
    }

    /// <summary>Decodes the payload fields into <paramref name="receipt"/>, deriving its status and logs from
    /// the frames, and closes the enclosing sequence at <paramref name="receiptEnd"/>.</summary>
    /// <remarks>The payload carries neither a transaction-level status nor a bloom, so a value taken from
    /// anywhere but the frames would not agree with a receipts-only node.</remarks>
    /// <param name="receiptEnd">The position the sequence enclosing the payload ends at.</param>
    /// <exception cref="RlpException">The payload holds no frames, or a frame is malformed.</exception>
    public static void DecodePayload(ref RlpReader reader, TxReceipt receipt, int receiptEnd, RlpBehaviors rlpBehaviors)
    {
        receipt.GasUsedTotal = reader.DecodeULong();
        receipt.Payer = reader.DecodeAddress();

        int framesEnd = reader.ReadSequenceLength() + reader.Position;
        int frameCount = reader.PeekNumberOfItemsRemaining(framesEnd, Eip8141Constants.MaxFrames + 1);
        reader.GuardLimit(frameCount, FrameReceiptsRlpLimit);
        if (frameCount == 0)
        {
            // A frame transaction has at least one frame, so a receipt without one is malformed.
            // Accepting it would derive a successful transaction status from nothing.
            ThrowEmptyFrameReceipts();
        }

        TxFrameReceipt[] frameReceipts = new TxFrameReceipt[frameCount];
        RlpLimit logsRlpLimit = LogsRlpLimit;
        int totalLogs = 0;
        for (int i = 0; i < frameCount; i++)
        {
            int frameEnd = reader.ReadSequenceLength() + reader.Position;
            byte status = reader.DecodeByte();
            // AggregateStatus folds anything but success to failure while RPC surfaces the raw byte,
            // so an out-of-range status would read differently at the two ends of the same receipt.
            GuardFrameStatus(status);

            int gasUsedEnd = reader.ReadSequenceLength() + reader.Position;
            ulong executionGasUsed = reader.DecodeULong();
            ulong stateGasUsed = reader.DecodeULong();
            reader.Check(gasUsedEnd);

            int logsEnd = reader.ReadSequenceLength() + reader.Position;
            int logCount = reader.PeekNumberOfItemsRemaining(logsEnd, logsRlpLimit.Limit + 1);
            reader.GuardLimit(logCount, logsRlpLimit);
            // The limit is derived from a whole transaction's gas, so it budgets the receipt rather than
            // each frame; spending it per frame would admit MaxFrames times the emissions it stands for.
            totalLogs += logCount;
            if (totalLogs > logsRlpLimit.Limit)
            {
                ThrowTooManyFrameLogs(totalLogs, logsRlpLimit.Limit);
            }

            LogEntry[] logs = new LogEntry[logCount];
            for (int j = 0; j < logCount; j++)
            {
                logs[j] = LogEntryDecoder.Instance.DecodeGuardNotNull(ref reader, RlpBehaviors.AllowExtraBytes);
            }

            reader.Check(logsEnd);

            frameReceipts[i] = new TxFrameReceipt(status, executionGasUsed, stateGasUsed, logs);
            reader.Check(frameEnd);
        }

        // The item count only requires a frame to start before the declared end, so an under-declared
        // frames header is only caught here; the enclosing checkpoint can land on it by coincidence.
        reader.Check(framesEnd);

        receipt.FrameReceipts = frameReceipts;
        receipt.StatusCode = TxFrameReceipt.AggregateStatus(frameReceipts);
        receipt.Logs = TxFrameReceipt.ConcatLogs(frameReceipts);

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != 0)
        {
            reader.Position = receiptEnd;
        }
        else
        {
            reader.Check(receiptEnd);
        }
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowUnknownFrameStatus(byte status)
        => throw new RlpException($"Frame receipt status {status} is not one of failure, success or skipped");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowMissingPayer()
        => throw new RlpException("Frame transaction receipt carries no payer");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowEmptyFrameReceipts()
        => throw new RlpException("Frame transaction receipt carries no frame receipts");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowTooManyFrames(int frameCount)
        => throw new RlpException($"Frame transaction receipt carries {frameCount} frame receipts, over the {Eip8141Constants.MaxFrames} a transaction may hold");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowTooManyFrameLogs(int totalLogs, int logsLimit)
        => throw new RlpException($"Frame transaction receipt carries {totalLogs} logs, over the {logsLimit} a receipt may hold");

    /// <summary>
    /// Decodes a stored frame receipt's <c>gas_used</c>, tolerating both the current
    /// <c>[execution, state]</c> list and the pre-2D scalar written by devnet7.
    /// </summary>
    /// <remarks>
    /// An RLP scalar sits below the sequence-prefix range, so it is unambiguous from the list.
    /// The scalar path attributes the whole value to execution (<c>state = 0</c>); read-compatibility
    /// only, removable once no node holds devnet7-era frame-tx receipts on disk.
    /// </remarks>
    private static void DecodeStoredGasUsed(ref RlpReader reader, out ulong execution, out ulong state)
    {
        if (reader.IsSequenceNext())
        {
            int gasUsedEnd = reader.ReadSequenceLength() + reader.Position;
            execution = reader.DecodeULong();
            state = reader.DecodeULong();
            reader.Check(gasUsedEnd);
        }
        else
        {
            execution = reader.DecodeULong();
            state = 0;
        }
    }

    /// <summary>Writes the frames list populated by <see cref="PopulateFramesLength"/>, its header included.</summary>
    private static void EncodeFrames<TWriter>(ref TWriter writer, TxFrameReceipt[] frameReceipts, IRlpDecoder<LogEntry?> logDecoder, ref LengthStack lengths)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        writer.StartSequence(lengths.Pop());
        for (int i = 0; i < frameReceipts.Length; i++)
        {
            TxFrameReceipt frameReceipt = frameReceipts[i];
            writer.StartSequence(lengths.Pop());
            writer.Encode((ulong)frameReceipt.Status);
            writer.StartSequence(GetGasUsedLength(frameReceipt));
            writer.Encode(frameReceipt.ExecutionGasUsed);
            writer.Encode(frameReceipt.StateGasUsed);
            logDecoder.EncodeSequence(ref writer, frameReceipt.Logs, ref lengths);
        }
    }

    private static int GetGasUsedLength(TxFrameReceipt frameReceipt) =>
        Rlp.LengthOf(frameReceipt.ExecutionGasUsed) + Rlp.LengthOf(frameReceipt.StateGasUsed);
}

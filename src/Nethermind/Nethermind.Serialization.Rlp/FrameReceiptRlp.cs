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
/// <remarks>The storage formats differ only in their log codec, so they pass their own, and they relax the
/// wire's bounds only where an older on-disk shape needs it. Every format writes through the same length and
/// encode paths, so a change to the frame layout cannot reach one of them alone.</remarks>
public static class FrameReceiptRlp
{
    /// <summary>The most logs a receipt may carry: a 100M gas ceiling still allows roughly 266k LOG0
    /// emissions after intrinsic gas.</summary>
    public const int MaxReceiptLogs = 270_000;

    private static readonly RlpLimit LogsRlpLimit = RlpLimit.For<TxReceipt>(MaxReceiptLogs, nameof(TxReceipt.Logs));
    private static readonly RlpLimit FrameReceiptsRlpLimit = RlpLimit.For<TxReceipt>(Eip8141Constants.MaxFrames, nameof(TxReceipt.FrameReceipts));

    /// <summary>The content length of the frames list, its own header excluded.</summary>
    internal static int GetFramesLength(TxFrameReceipt[] frameReceipts, IRlpDecoder<LogEntry?> logDecoder)
    {
        int framesLength = 0;
        for (int i = 0; i < frameReceipts.Length; i++)
        {
            framesLength += Rlp.LengthOfSequence(GetFrameLength(frameReceipts[i], logDecoder));
        }

        return framesLength;
    }

    /// <summary>Writes the frames list, its header included.</summary>
    internal static void EncodeFrames<TWriter>(ref TWriter writer, TxFrameReceipt[] frameReceipts, IRlpDecoder<LogEntry?> logDecoder)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
        => EncodeFrames(ref writer, frameReceipts, logDecoder, GetFramesLength(frameReceipts, logDecoder));

    /// <summary>Decodes the frames list of a stored receipt's <c>[payer, [frame_receipt, ...]]</c> extension.</summary>
    /// <remarks>Lenient only where the on-disk history demands it — a pre-2D scalar <c>gas_used</c>, and an empty
    /// frames list. The frame count and the status byte are bounded as on the wire, so a datadir cannot hold a
    /// receipt this node reads back but no peer would accept.</remarks>
    internal static TxFrameReceipt[] DecodeStoredFrames(ref RlpReader reader, IRlpDecoder<LogEntry?> logDecoder)
    {
        int framesEnd = reader.ReadSequenceLength() + reader.Position;
        int frameCount = reader.PeekNumberOfItemsRemaining(framesEnd, Eip8141Constants.MaxFrames + 1);
        reader.GuardLimit(frameCount, FrameReceiptsRlpLimit);

        TxFrameReceipt[] frameReceipts = new TxFrameReceipt[frameCount];
        for (int i = 0; i < frameCount; i++)
        {
            int frameEnd = reader.ReadSequenceLength() + reader.Position;
            byte status = reader.DecodeByte();
            GuardFrameStatus(status);
            DecodeStoredGasUsed(ref reader, out ulong executionGasUsed, out ulong stateGasUsed);

            int logsEnd = reader.ReadSequenceLength() + reader.Position;
            using ArrayPoolListRef<LogEntry> frameLogs = new(4);
            while (reader.Position < logsEnd)
            {
                frameLogs.Add(logDecoder.DecodeGuardNotNull(ref reader, RlpBehaviors.AllowExtraBytes));
            }

            frameReceipts[i] = new TxFrameReceipt(status, executionGasUsed, stateGasUsed, frameLogs.ToArray());
            reader.Check(frameEnd);
        }

        // Counting items only requires a frame to start before the declared end, so an under-declared
        // frames header is only caught here.
        reader.Check(framesEnd);
        return frameReceipts;
    }

    /// <summary>The content length of a stored receipt's trailing <c>[payer, frames]</c> extension.</summary>
    /// <exception cref="RlpException">The receipt carries no payer.</exception>
    internal static int GetStoredExtensionLength(TxReceipt receipt, IRlpDecoder<LogEntry?> logDecoder)
    {
        GuardPayer(receipt);
        return Rlp.LengthOf(receipt.Payer)
               + Rlp.LengthOfSequence(GetFramesLength(receipt.FrameReceipts ?? [], logDecoder));
    }

    /// <summary>Writes a stored receipt's trailing <c>[payer, frames]</c> extension.</summary>
    /// <exception cref="RlpException">The receipt carries no payer.</exception>
    internal static void EncodeStoredExtension<TWriter>(ref TWriter writer, TxReceipt receipt, IRlpDecoder<LogEntry?> logDecoder)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        GuardPayer(receipt);
        writer.Encode(receipt.Payer);
        EncodeFrames(ref writer, receipt.FrameReceipts ?? [], logDecoder);
    }

    /// <summary>Rejects a receipt the strict read side could not decode back, rather than persisting it.</summary>
    private static void GuardPayer(TxReceipt receipt)
    {
        if (receipt.Payer is null)
        {
            ThrowMissingPayer();
        }
    }

    private static void GuardFrameStatus(byte status)
    {
        if (status is not (TxFrameReceipt.StatusFailure or TxFrameReceipt.StatusSuccess or TxFrameReceipt.StatusSkipped))
        {
            ThrowUnknownFrameStatus(status);
        }
    }

    /// <summary>The content length of the payload fields <c>[cumulative_gas_used, payer, frames]</c>,
    /// the sequence enclosing them excluded.</summary>
    /// <param name="receipt">The frame-transaction receipt to measure.</param>
    /// <param name="framesLength">The content length of the frames list, to hand back to the encoder.</param>
    /// <exception cref="RlpException">The receipt carries no payer.</exception>
    public static int GetPayloadLength(TxReceipt receipt, out int framesLength)
    {
        GuardPayer(receipt);
        framesLength = GetFramesLength(receipt.FrameReceipts ?? [], LogEntryDecoder.Instance);
        return Rlp.LengthOf(receipt.GasUsedTotal)
               + Rlp.LengthOf(receipt.Payer)
               + Rlp.LengthOfSequence(framesLength);
    }

    /// <summary>Writes the payload fields; the caller owns the sequence enclosing them.</summary>
    public static void EncodePayload<TWriter>(ref TWriter writer, TxReceipt receipt, int framesLength)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        writer.Encode(receipt.GasUsedTotal);
        writer.Encode(receipt.Payer);
        EncodeFrames(ref writer, receipt.FrameReceipts ?? [], LogEntryDecoder.Instance, framesLength);
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
            int logCount = reader.PeekNumberOfItemsRemaining(logsEnd, LogsRlpLimit.Limit + 1);
            reader.GuardLimit(logCount, LogsRlpLimit);
            // The limit is derived from a whole transaction's gas, so it budgets the receipt rather than
            // each frame; spending it per frame would admit MaxFrames times the emissions it stands for.
            totalLogs += logCount;
            if (totalLogs > LogsRlpLimit.Limit)
            {
                ThrowTooManyFrameLogs(totalLogs);
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

        [DoesNotReturn, StackTraceHidden]
        static void ThrowEmptyFrameReceipts()
            => throw new RlpException("Frame transaction receipt carries no frame receipts");

        [DoesNotReturn, StackTraceHidden]
        static void ThrowTooManyFrameLogs(int totalLogs)
            => throw new RlpException($"Frame transaction receipt carries {totalLogs} logs, over the {LogsRlpLimit.Limit} a receipt may hold");
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowUnknownFrameStatus(byte status)
        => throw new RlpException($"Frame receipt status {status} is not one of failure, success or skipped");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowMissingPayer()
        => throw new RlpException("Frame transaction receipt carries no payer");

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

    private static void EncodeFrames<TWriter>(ref TWriter writer, TxFrameReceipt[] frameReceipts, IRlpDecoder<LogEntry?> logDecoder, int framesLength)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        writer.StartSequence(framesLength);
        for (int i = 0; i < frameReceipts.Length; i++)
        {
            TxFrameReceipt frameReceipt = frameReceipts[i];
            int gasUsedLength = GetGasUsedLength(frameReceipt);
            int logsLength = GetLogsLength(frameReceipt, logDecoder);
            writer.StartSequence(Rlp.LengthOf((ulong)frameReceipt.Status)
                                 + Rlp.LengthOfSequence(gasUsedLength)
                                 + Rlp.LengthOfSequence(logsLength));
            writer.Encode((ulong)frameReceipt.Status);
            writer.StartSequence(gasUsedLength);
            writer.Encode(frameReceipt.ExecutionGasUsed);
            writer.Encode(frameReceipt.StateGasUsed);
            writer.StartSequence(logsLength);
            for (int j = 0; j < frameReceipt.Logs.Length; j++)
            {
                logDecoder.Encode(ref writer, frameReceipt.Logs[j]);
            }
        }
    }

    private static int GetFrameLength(TxFrameReceipt frameReceipt, IRlpDecoder<LogEntry?> logDecoder) =>
        Rlp.LengthOf((ulong)frameReceipt.Status)
        + Rlp.LengthOfSequence(GetGasUsedLength(frameReceipt))
        + Rlp.LengthOfSequence(GetLogsLength(frameReceipt, logDecoder));

    private static int GetGasUsedLength(TxFrameReceipt frameReceipt) =>
        Rlp.LengthOf(frameReceipt.ExecutionGasUsed) + Rlp.LengthOf(frameReceipt.StateGasUsed);

    private static int GetLogsLength(TxFrameReceipt frameReceipt, IRlpDecoder<LogEntry?> logDecoder)
    {
        int logsLength = 0;
        for (int i = 0; i < frameReceipt.Logs.Length; i++)
        {
            logsLength += logDecoder.GetLength(frameReceipt.Logs[i]);
        }

        return logsLength;
    }
}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Serialization.FluentRlp;

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

    /// <summary>The content length of the frames list, its own header excluded.</summary>
    private static int GetFramesLength(TxFrameReceipt[] frameReceipts, IRlpDecoder<LogEntry?> logDecoder)
    {
        int framesLength = 0;
        for (int i = 0; i < frameReceipts.Length; i++)
        {
            framesLength += Rlp.LengthOfSequence(GetFrameLength(frameReceipts[i], logDecoder));
        }

        return framesLength;
    }

    /// <summary>Writes the frames list, its header included.</summary>
    private static void EncodeFrames(ref RlpWriter writer, TxFrameReceipt[] frameReceipts, IRlpDecoder<LogEntry?> logDecoder)
        => EncodeFrames(ref writer, frameReceipts, logDecoder, GetFramesLength(frameReceipts, logDecoder));

    /// <summary>Decodes the frames list of a stored receipt's <c>[payer, [frame_receipt, ...]]</c> extension.</summary>
    /// <remarks>Lenient by design: the data is locally written, and it may predate the 2D <c>gas_used</c>.</remarks>
    /// <param name="rlpBehaviors">The behaviors of the receipt decode; only <see cref="RlpBehaviors.TrustedInput"/> goes to the log decoder.</param>
    internal static TxFrameReceipt[] DecodeStoredFrames<TCanonicalChecks>(ref RlpReader reader, IRlpDecoder<LogEntry?> logDecoder, RlpBehaviors rlpBehaviors)
        where TCanonicalChecks : struct, IFlag
    {
        RlpBehaviors logBehaviors = RlpBehaviors.AllowExtraBytes | (rlpBehaviors & RlpBehaviors.TrustedInput);
        RlpReader frames = reader.EnterSequence<TCanonicalChecks>(out int framesLength);
        TxFrameReceipt[] frameReceipts = new TxFrameReceipt[CountAll<TCanonicalChecks>(in frames)];
        for (int i = 0; i < frameReceipts.Length; i++)
        {
            RlpReader frame = frames.EnterSequence<TCanonicalChecks>(out int frameLength);
            byte status = frame.DecodeByte<TCanonicalChecks>();
            DecodeStoredGasUsed<TCanonicalChecks>(ref frame, out ulong executionGasUsed, out ulong stateGasUsed);

            RlpReader logs = frame.EnterSequence<TCanonicalChecks>(out int logsLength);
            LogEntry[] frameLogs = new LogEntry[CountAll<TCanonicalChecks>(in logs)];
            for (int j = 0; j < frameLogs.Length; j++)
            {
                frameLogs[j] = logDecoder.DecodeGuardNotNull(ref logs, logBehaviors);
            }

            frame.ExitSequence(in logs, logsLength);
            frameReceipts[i] = new TxFrameReceipt(status, executionGasUsed, stateGasUsed, frameLogs);
            frames.ExitSequence(in frame, frameLength);
        }

        reader.ExitSequence(in frames, framesLength);
        return frameReceipts;
    }

    /// <summary>Counts the items of a stored list, which has no count limit.</summary>
    /// <remarks>Each item takes at least one byte, so the count cannot reach the bound.</remarks>
    private static int CountAll<TCanonicalChecks>(scoped in RlpReader reader) where TCanonicalChecks : struct, IFlag
        => reader.CountItems<TCanonicalChecks>(reader.Remaining + 1);

    /// <summary>The content length of a stored receipt's trailing <c>[payer, frames]</c> extension.</summary>
    /// <exception cref="RlpReaderException">The receipt is one no read path would take back.</exception>
    internal static int GetStoredExtensionLength(TxReceipt receipt, IRlpDecoder<LogEntry?> logDecoder)
    {
        TxFrameReceipt[] frameReceipts = GuardEncodable(receipt);
        return Rlp.LengthOf(receipt.Payer) + Rlp.LengthOfSequence(GetFramesLength(frameReceipts, logDecoder));
    }

    /// <summary>Writes a stored receipt's trailing <c>[payer, frames]</c> extension.</summary>
    /// <exception cref="RlpReaderException">The receipt is one no read path would take back.</exception>
    internal static void EncodeStoredExtension(ref RlpWriter writer, TxReceipt receipt, IRlpDecoder<LogEntry?> logDecoder)
    {
        TxFrameReceipt[] frameReceipts = GuardEncodable(receipt);
        writer.Encode(receipt.Payer);
        EncodeFrames(ref writer, frameReceipts, logDecoder);
    }

    /// <summary>Rejects a receipt whose shape a read path would refuse, rather than writing it.</summary>
    /// <remarks>The bounds are held here rather than in <see cref="DecodeStoredFrames"/> because a stored row
    /// that fails to parse takes its whole block's receipt array with it, and no encoder could reproduce the
    /// bytes to migrate it. Refusing on the way in keeps a row already on disk readable.</remarks>
    /// <returns>The receipt's frame receipts, once they are known to be encodable.</returns>
    /// <exception cref="RlpReaderException">The receipt is one no read path would take back.</exception>
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

    /// <summary>The content length of the payload fields <c>[cumulative_gas_used, payer, frames]</c>,
    /// the sequence enclosing them excluded.</summary>
    /// <param name="receipt">The frame-transaction receipt to measure.</param>
    /// <param name="framesLength">The content length of the frames list, to hand back to the encoder.</param>
    /// <exception cref="RlpReaderException">The receipt is one no read path would take back.</exception>
    public static int GetPayloadLength(TxReceipt receipt, out int framesLength)
    {
        framesLength = GetFramesLength(GuardEncodable(receipt), LogEntryDecoder.Instance);
        return Rlp.LengthOf(receipt.GasUsedTotal)
               + Rlp.LengthOf(receipt.Payer)
               + Rlp.LengthOfSequence(framesLength);
    }

    /// <summary>Writes the payload fields; the caller owns the sequence enclosing them.</summary>
    /// <exception cref="RlpReaderException">The receipt is one no read path would take back.</exception>
    public static void EncodePayload(ref RlpWriter writer, TxReceipt receipt, int framesLength)
    {
        TxFrameReceipt[] frameReceipts = GuardEncodable(receipt);
        writer.Encode(receipt.GasUsedTotal);
        writer.Encode(receipt.Payer);
        EncodeFrames(ref writer, frameReceipts, LogEntryDecoder.Instance, framesLength);
    }

    /// <summary>Decodes the payload fields into <paramref name="receipt"/>, deriving its status and logs from
    /// the frames.</summary>
    /// <remarks>The payload carries neither a transaction-level status nor a bloom, so a value taken from
    /// anywhere but the frames would not agree with a receipts-only node.</remarks>
    /// <param name="fields">The reader of the sequence enclosing the payload; the caller ends the sequence.</param>
    /// <param name="rlpBehaviors">The behaviors of the decode; only <see cref="RlpBehaviors.TrustedInput"/> applies.</param>
    /// <exception cref="RlpReaderException">The payload holds no frames, or a frame is malformed.</exception>
    public static void DecodePayload(ref RlpReader fields, TxReceipt receipt, RlpBehaviors rlpBehaviors)
    {
        if ((rlpBehaviors & RlpBehaviors.TrustedInput) != 0)
        {
            DecodePayload<OffFlag>(ref fields, receipt);
        }
        else
        {
            DecodePayload<OnFlag>(ref fields, receipt);
        }
    }

    /// <inheritdoc cref="DecodePayload(ref RlpReader, TxReceipt, RlpBehaviors)"/>
    internal static void DecodePayload<TCanonicalChecks>(ref RlpReader fields, TxReceipt receipt) where TCanonicalChecks : struct, IFlag
    {
        RlpBehaviors logBehaviors = TCanonicalChecks.IsActive ? RlpBehaviors.AllowExtraBytes : RlpBehaviors.AllowExtraBytes | RlpBehaviors.TrustedInput;
        receipt.GasUsedTotal = fields.DecodeULong<TCanonicalChecks>();
        receipt.Payer = fields.DecodeAddress();

        RlpReader frames = fields.EnterSequence<TCanonicalChecks>(out int framesLength);
        int frameCount = frames.CountItems<TCanonicalChecks>(FrameReceiptsRlpLimit);
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
            RlpReader frame = frames.EnterSequence<TCanonicalChecks>(out int frameLength);
            byte status = frame.DecodeByte<TCanonicalChecks>();
            // AggregateStatus folds anything but success to failure while RPC surfaces the raw byte,
            // so an out-of-range status would read differently at the two ends of the same receipt.
            GuardFrameStatus(status);

            RlpReader gasUsed = frame.EnterSequence<TCanonicalChecks>(out int gasUsedLength);
            ulong executionGasUsed = gasUsed.DecodeULong<TCanonicalChecks>();
            ulong stateGasUsed = gasUsed.DecodeULong<TCanonicalChecks>();
            frame.ExitSequence(in gasUsed, gasUsedLength);

            RlpReader logs = frame.EnterSequence<TCanonicalChecks>(out int logsLength);
            int logCount = logs.CountItems<TCanonicalChecks>(logsRlpLimit);
            // The limit is derived from a whole transaction's gas, so it budgets the receipt rather than
            // each frame; spending it per frame would admit MaxFrames times the emissions it stands for.
            totalLogs += logCount;
            if (totalLogs > logsRlpLimit.Limit)
            {
                ThrowTooManyFrameLogs(totalLogs, logsRlpLimit.Limit);
            }

            LogEntry[] frameLogs = new LogEntry[logCount];
            for (int j = 0; j < logCount; j++)
            {
                frameLogs[j] = LogEntryDecoder.Instance.DecodeGuardNotNull(ref logs, logBehaviors);
            }

            frame.ExitSequence(in logs, logsLength);
            frameReceipts[i] = new TxFrameReceipt(status, executionGasUsed, stateGasUsed, frameLogs);
            frames.ExitSequence(in frame, frameLength);
        }

        fields.ExitSequence(in frames, framesLength);

        receipt.FrameReceipts = frameReceipts;
        receipt.StatusCode = TxFrameReceipt.AggregateStatus(frameReceipts);
        receipt.Logs = TxFrameReceipt.ConcatLogs(frameReceipts);
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowUnknownFrameStatus(byte status)
        => throw new RlpReaderException($"Frame receipt status {status} is not one of failure, success or skipped");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowMissingPayer()
        => throw new RlpReaderException("Frame transaction receipt carries no payer");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowEmptyFrameReceipts()
        => throw new RlpReaderException("Frame transaction receipt carries no frame receipts");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowTooManyFrames(int frameCount)
        => throw new RlpReaderException($"Frame transaction receipt carries {frameCount} frame receipts, over the {Eip8141Constants.MaxFrames} a transaction may hold");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowTooManyFrameLogs(int totalLogs, int logsLimit)
        => throw new RlpReaderException($"Frame transaction receipt carries {totalLogs} logs, over the {logsLimit} a receipt may hold");

    /// <summary>
    /// Decodes a stored frame receipt's <c>gas_used</c>, tolerating both the current
    /// <c>[execution, state]</c> list and the pre-2D scalar written by devnet7.
    /// </summary>
    /// <remarks>
    /// An RLP scalar sits below the sequence-prefix range, so it is unambiguous from the list.
    /// The scalar path attributes the whole value to execution (<c>state = 0</c>); read-compatibility
    /// only, removable once no node holds devnet7-era frame-tx receipts on disk.
    /// </remarks>
    private static void DecodeStoredGasUsed<TCanonicalChecks>(ref RlpReader reader, out ulong execution, out ulong state)
        where TCanonicalChecks : struct, IFlag
    {
        if (reader.IsNextSequence)
        {
            RlpReader gasUsed = reader.EnterSequence<TCanonicalChecks>(out int gasUsedLength);
            execution = gasUsed.DecodeULong<TCanonicalChecks>();
            state = gasUsed.DecodeULong<TCanonicalChecks>();
            reader.ExitSequence(in gasUsed, gasUsedLength);
        }
        else
        {
            execution = reader.DecodeULong<TCanonicalChecks>();
            state = 0;
        }
    }

    private static void EncodeFrames(ref RlpWriter writer, TxFrameReceipt[] frameReceipts, IRlpDecoder<LogEntry?> logDecoder, int framesLength)
    {
        RlpSequence frames = writer.StartSequence(framesLength);
        for (int i = 0; i < frameReceipts.Length; i++)
        {
            TxFrameReceipt frameReceipt = frameReceipts[i];
            int gasUsedLength = GetGasUsedLength(frameReceipt);
            int logsLength = GetLogsLength(frameReceipt, logDecoder);
            RlpSequence frame = writer.StartSequence(Rlp.LengthOf((ulong)frameReceipt.Status)
                                 + Rlp.LengthOfSequence(gasUsedLength)
                                 + Rlp.LengthOfSequence(logsLength));
            writer.Encode((ulong)frameReceipt.Status);
            RlpSequence gasUsed = writer.StartSequence(gasUsedLength);
            writer.Encode(frameReceipt.ExecutionGasUsed);
            writer.Encode(frameReceipt.StateGasUsed);
            writer.EndSequence(gasUsed);
            RlpSequence logs = writer.StartSequence(logsLength);
            for (int j = 0; j < frameReceipt.Logs.Length; j++)
            {
                logDecoder.Encode(ref writer, frameReceipt.Logs[j]);
            }

            writer.EndSequence(logs);
            writer.EndSequence(frame);
        }

        writer.EndSequence(frames);
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

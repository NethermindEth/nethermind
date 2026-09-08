// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.Serialization.Rlp;

/// <summary>
/// Static-abstract policy naming the log codec that a receipt storage format encodes its logs with.
/// </summary>
/// <typeparam name="TSelf">The implementing policy type.</typeparam>
/// <remarks>
/// Implemented by empty structs so that <see cref="FrameReceiptRlp{TLogCodec}"/> gets a JIT instantiation
/// per storage format and the log codec calls stay direct rather than going through a virtual dispatch.
/// </remarks>
internal interface ILogEntryCodec<TSelf> where TSelf : struct, ILogEntryCodec<TSelf>
{
    static abstract LogEntry Decode(ref RlpReader reader, RlpBehaviors rlpBehaviors);

    static abstract void Encode<TWriter>(ref TWriter writer, LogEntry log)
        where TWriter : struct, IRlpWriteBackend, allows ref struct;

    static abstract int GetLength(LogEntry log);
}

/// <summary>The log codec of <see cref="ReceiptStorageDecoder"/>.</summary>
internal readonly struct StandardLogEntryCodec : ILogEntryCodec<StandardLogEntryCodec>
{
    public static LogEntry Decode(ref RlpReader reader, RlpBehaviors rlpBehaviors)
        => LogEntryDecoder.Instance.DecodeGuardNotNull(ref reader, rlpBehaviors);

    public static void Encode<TWriter>(ref TWriter writer, LogEntry log)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
        => LogEntryDecoder.Instance.Encode(ref writer, log);

    public static int GetLength(LogEntry log) => LogEntryDecoder.Instance.GetLength(log);
}

/// <summary>The log codec of <see cref="CompactReceiptStorageDecoder"/>.</summary>
internal readonly struct CompactLogEntryCodec : ILogEntryCodec<CompactLogEntryCodec>
{
    public static LogEntry Decode(ref RlpReader reader, RlpBehaviors rlpBehaviors)
        => CompactLogEntryDecoder.Instance.DecodeGuardNotNull(ref reader, rlpBehaviors);

    public static void Encode<TWriter>(ref TWriter writer, LogEntry log)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
        => CompactLogEntryDecoder.Instance.Encode(ref writer, log);

    public static int GetLength(LogEntry log) => CompactLogEntryDecoder.Instance.GetLength(log);
}

/// <summary>
/// Codec for the EIP-8141 per-frame receipt sequence
/// <c>[[status, [execution_gas_used, state_gas_used], [log, ...]], ...]</c> that a frame-tx receipt carries
/// after the standard storage fields.
/// </summary>
/// <typeparam name="TLogCodec">The log codec of the enclosing storage format.</typeparam>
/// <remarks>
/// Shared by <see cref="ReceiptStorageDecoder"/> and <see cref="CompactReceiptStorageDecoder"/>. The two
/// storage formats differ only in how a single log entry is encoded, so the frame layout lives here to stop
/// them drifting apart.
/// </remarks>
internal static class FrameReceiptRlp<TLogCodec> where TLogCodec : struct, ILogEntryCodec<TLogCodec>
{
    public static TxFrameReceipt[] Decode(ref RlpReader reader)
    {
        int framesEnd = reader.ReadSequenceLength() + reader.Position;
        using ArrayPoolListRef<TxFrameReceipt> frameReceipts = new(Eip8141Constants.MaxFrames);
        while (reader.Position < framesEnd)
        {
            int frameEnd = reader.ReadSequenceLength() + reader.Position;
            byte status = reader.DecodeByte();
            FrameReceiptGasRlp.DecodeGasUsed(ref reader, out ulong executionGasUsed, out ulong stateGasUsed);

            int logsEnd = reader.ReadSequenceLength() + reader.Position;
            using ArrayPoolListRef<LogEntry> frameLogs = new(4);
            while (reader.Position < logsEnd)
            {
                frameLogs.Add(TLogCodec.Decode(ref reader, RlpBehaviors.AllowExtraBytes));
            }

            frameReceipts.Add(new TxFrameReceipt(status, executionGasUsed, stateGasUsed, frameLogs.ToArray()));
            reader.Check(frameEnd);
        }

        return frameReceipts.ToArray();
    }

    public static void Encode<TWriter>(ref TWriter writer, TxFrameReceipt[] frameReceipts)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        writer.StartSequence(GetContentLength(frameReceipts));
        for (int i = 0; i < frameReceipts.Length; i++)
        {
            TxFrameReceipt frameReceipt = frameReceipts[i];
            int gasUsedLength = FrameReceiptGasRlp.GetGasUsedContentLength(frameReceipt.ExecutionGasUsed, frameReceipt.StateGasUsed);
            int logsLength = GetLogsLength(frameReceipt);
            writer.StartSequence(GetFrameReceiptContentLength(frameReceipt, gasUsedLength, logsLength));
            writer.Encode((ulong)frameReceipt.Status);
            writer.StartSequence(gasUsedLength);
            writer.Encode(frameReceipt.ExecutionGasUsed);
            writer.Encode(frameReceipt.StateGasUsed);
            writer.StartSequence(logsLength);
            for (int j = 0; j < frameReceipt.Logs.Length; j++)
            {
                TLogCodec.Encode(ref writer, frameReceipt.Logs[j]);
            }
        }
    }

    /// <summary>Returns the encoded length of <paramref name="frameReceipts"/>, including the sequence prefix.</summary>
    public static int GetLength(TxFrameReceipt[] frameReceipts) => Rlp.LengthOfSequence(GetContentLength(frameReceipts));

    private static int GetContentLength(TxFrameReceipt[] frameReceipts)
    {
        int framesLength = 0;
        for (int i = 0; i < frameReceipts.Length; i++)
        {
            framesLength += Rlp.LengthOfSequence(GetFrameReceiptContentLength(frameReceipts[i]));
        }

        return framesLength;
    }

    private static int GetFrameReceiptContentLength(TxFrameReceipt frameReceipt) =>
        GetFrameReceiptContentLength(
            frameReceipt,
            FrameReceiptGasRlp.GetGasUsedContentLength(frameReceipt.ExecutionGasUsed, frameReceipt.StateGasUsed),
            GetLogsLength(frameReceipt));

    private static int GetFrameReceiptContentLength(TxFrameReceipt frameReceipt, int gasUsedLength, int logsLength) =>
        Rlp.LengthOf((ulong)frameReceipt.Status) + Rlp.LengthOfSequence(gasUsedLength) + Rlp.LengthOfSequence(logsLength);

    private static int GetLogsLength(TxFrameReceipt frameReceipt)
    {
        int logsLength = 0;
        for (int i = 0; i < frameReceipt.Logs.Length; i++)
        {
            logsLength += TLogCodec.GetLength(frameReceipt.Logs[i]);
        }

        return logsLength;
    }
}

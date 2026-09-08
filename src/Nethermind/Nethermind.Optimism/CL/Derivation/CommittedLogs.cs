// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.JsonRpc.Data;

namespace Nethermind.Optimism.CL.Derivation;

/// <summary>The logs of an L1 receipt whose emitting execution committed, the only ones derivation may read.</summary>
internal static class CommittedLogs
{
    /// <summary>Enumerates the receipt's logs, dropping those the L1 execution rolled back.</summary>
    /// <remarks>
    /// EIP-8141 carries no transaction-level status: the receipt status is the aggregate over the frames, so
    /// gating on it discards logs an independent frame committed. The transaction's log set is the frame logs
    /// concatenated in frame order, so each frame claims the next run of its own length. Frames that do not
    /// reproduce exactly the logs the receipt carries are unattributable and stay gated on the aggregate.
    /// </remarks>
    /// <exception cref="ArgumentException">A log entry is null.</exception>
    public static IEnumerable<LogEntryForRpc> Of(ReceiptForRpc receipt)
    {
        // An L1 node omits "logs" or sends null for a receipt with no logs.
        LogEntryForRpc[] logs = receipt.Logs ?? [];
        for (int i = 0; i < logs.Length; i++)
        {
            if (logs[i] is null) throw new ArgumentException($"Log entry {i} of receipt {receipt.TransactionHash} is null");
        }

        if (TryGetAttributableFrames(receipt, logs, out FrameReceiptForRpc[]? frames))
        {
            int start = 0;
            foreach (FrameReceiptForRpc frame in frames)
            {
                int count = frame.Logs?.Length ?? 0;
                if (frame.Status == TxFrameReceipt.StatusSuccess)
                {
                    for (int i = start; i < start + count; i++) yield return logs[i];
                }

                start += count;
            }
        }
        else if (receipt.Status == StatusCode.Success)
        {
            foreach (LogEntryForRpc log in logs) yield return log;
        }
    }

    private static bool TryGetAttributableFrames(ReceiptForRpc receipt, LogEntryForRpc[] logs, [NotNullWhen(true)] out FrameReceiptForRpc[]? frames)
    {
        frames = receipt.FrameReceipts;
        if (receipt.Type != TxType.FrameTx || frames is not { Length: > 0 }) return false;

        int matched = 0;
        foreach (FrameReceiptForRpc frame in frames)
        {
            if (frame is null) return false;

            foreach (LogEntry frameLog in frame.Logs ?? [])
            {
                if (matched == logs.Length || !IsSameLog(logs[matched], frameLog)) return false;
                matched++;
            }
        }

        return matched == logs.Length;
    }

    /// <summary>Whether the receipt log is the frame log the concatenation puts at its position.</summary>
    /// <remarks>The counts alone would let a payload place a committed frame's run over a reverted frame's log.</remarks>
    private static bool IsSameLog(LogEntryForRpc log, LogEntry frameLog) =>
        frameLog is not null
        && log.Address == frameLog.Address
        && HaveSameTopics(log.Topics, frameLog.Topics)
        && Bytes.AreEqual(log.Data, frameLog.Data);

    private static bool HaveSameTopics(ReadOnlySpan<Hash256> left, ReadOnlySpan<Hash256> right)
    {
        if (left.Length != right.Length) return false;

        for (int i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i]) return false;
        }

        return true;
    }
}

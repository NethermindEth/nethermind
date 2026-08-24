// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core;

/// <summary>
/// A per-frame receipt entry of an EIP-8141 frame transaction: <c>[status, gas_used, logs]</c>,
/// where <c>gas_used = [execution, state]</c>.
/// https://eips.ethereum.org/EIPS/eip-8141
/// </summary>
public class TxFrameReceipt(byte status, ulong executionGasUsed, ulong stateGasUsed, LogEntry[] logs)
{
    public const byte StatusFailure = 0;
    public const byte StatusSuccess = 1;

    /// <summary>Frames skipped by a failed atomic batch.</summary>
    public const byte StatusSkipped = 2;

    public byte Status { get; } = status;

    /// <summary>Execution gas used by the frame (<c>gas_used.execution</c>), not accounting for refunds.</summary>
    public ulong ExecutionGasUsed { get; } = executionGasUsed;

    /// <summary>State gas attributed to the frame (<c>gas_used.state</c>) after all refills and rollbacks.</summary>
    public ulong StateGasUsed { get; } = stateGasUsed;

    /// <summary>The frame's combined gas: execution plus state.</summary>
    public ulong GasUsed => ExecutionGasUsed + StateGasUsed;

    public LogEntry[] Logs { get; } = logs;

    /// <summary>The transaction-level status reported for a frame transaction with these frame receipts.</summary>
    /// <remarks>
    /// The EIP-8141 receipt payload carries no transaction-level status, so the value JSON-RPC reports
    /// has to be derived. Deriving it from the frame statuses — success only when every frame
    /// succeeded — keeps a node that executed the block and a node that only received its receipts in
    /// agreement, which a value taken from local execution state cannot do.
    /// </remarks>
    public static byte AggregateStatus(ReadOnlySpan<TxFrameReceipt> frameReceipts)
    {
        foreach (TxFrameReceipt frameReceipt in frameReceipts)
        {
            if (frameReceipt.Status != StatusSuccess)
            {
                return StatusFailure;
            }
        }

        return StatusSuccess;
    }

    /// <summary>The transaction's log set: the frame logs in frame order.</summary>
    /// <remarks>
    /// Derived rather than accumulated in parallel, so a frame whose logs are dropped — an unrolled
    /// atomic batch, a body discarded by a failed EIP-7906 assertion — also drops out of the bloom.
    /// </remarks>
    public static LogEntry[] ConcatLogs(TxFrameReceipt[] frameReceipts)
    {
        int total = 0;
        foreach (TxFrameReceipt frameReceipt in frameReceipts)
        {
            total += frameReceipt.Logs.Length;
        }

        if (total == 0) return [];

        LogEntry[] logs = new LogEntry[total];
        int offset = 0;
        foreach (TxFrameReceipt frameReceipt in frameReceipts)
        {
            frameReceipt.Logs.CopyTo(logs, offset);
            offset += frameReceipt.Logs.Length;
        }

        return logs;
    }
}

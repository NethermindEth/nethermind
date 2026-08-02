// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>
/// A per-frame receipt entry of an EIP-8141 frame transaction: <c>[status, gas_used, logs]</c>.
/// https://eips.ethereum.org/EIPS/eip-8141
/// </summary>
public class TxFrameReceipt(byte status, ulong gasUsed, LogEntry[] logs)
{
    public const byte StatusFailure = 0;
    public const byte StatusSuccess = 1;

    /// <summary>Frames skipped by a failed atomic batch.</summary>
    public const byte StatusSkipped = 2;

    public byte Status { get; } = status;
    public ulong GasUsed { get; } = gasUsed;
    public LogEntry[] Logs { get; } = logs;

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

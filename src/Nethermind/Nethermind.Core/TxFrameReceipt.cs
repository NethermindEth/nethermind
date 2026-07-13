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
    public const byte StatusSkipped = 3;

    public byte Status { get; } = status;
    public ulong GasUsed { get; } = gasUsed;
    public LogEntry[] Logs { get; } = logs;
}

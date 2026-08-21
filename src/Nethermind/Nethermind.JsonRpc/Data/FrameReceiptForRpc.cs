// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.JsonRpc.Data;

/// <summary>JSON-RPC view of an EIP-8141 per-frame receipt: <c>[status, gas_used, logs]</c>.</summary>
public class FrameReceiptForRpc
{
    public FrameReceiptForRpc()
    {
    }

    public FrameReceiptForRpc(TxFrameReceipt frameReceipt)
    {
        Status = frameReceipt.Status;
        GasUsed = frameReceipt.GasUsed;
        Logs = frameReceipt.Logs;
    }

    public byte Status { get; set; }
    public ulong GasUsed { get; set; }

    /// <summary>Nullable because a caller can send <c>"logs": null</c>, which the deserializer honours.</summary>
    public LogEntry[]? Logs { get; set; } = [];

    public TxFrameReceipt ToFrameReceipt() => new(Status, GasUsed, Logs ?? []);
}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Data;

/// <summary>JSON-RPC view of an EIP-8141 per-frame receipt: <c>[status, gas_used, logs]</c>.</summary>
public class FrameReceiptForRpc
{
    public FrameReceiptForRpc()
    {
    }

    /// <param name="frameReceipt">The frame's receipt entry.</param>
    /// <param name="logs">The frame's slice of the transaction receipt's own RPC logs.</param>
    public FrameReceiptForRpc(TxFrameReceipt frameReceipt, LogEntryForRpc[] logs)
    {
        Status = frameReceipt.Status;
        ExecutionGasUsed = frameReceipt.ExecutionGasUsed;
        StateGasUsed = frameReceipt.StateGasUsed;
        Logs = logs;
    }

    [JsonConverter(typeof(ByteConverter))]
    public byte Status { get; set; }

    /// <summary>The frame's combined gas: <see cref="ExecutionGasUsed"/> plus <see cref="StateGasUsed"/>.</summary>
    public ulong GasUsed => ExecutionGasUsed + StateGasUsed;

    public ulong ExecutionGasUsed { get; set; }
    public ulong StateGasUsed { get; set; }

    /// <summary>The frame's log entries.</summary>
    /// <remarks>Nullable because a caller can send <c>"logs": null</c>, which the deserializer honours.</remarks>
    public LogEntryForRpc[]? Logs { get; set; } = [];

    public TxFrameReceipt ToFrameReceipt()
    {
        LogEntryForRpc[] logs = Logs ?? [];
        LogEntry[] logEntries = new LogEntry[logs.Length];
        for (int i = 0; i < logs.Length; i++)
        {
            logEntries[i] = logs[i]?.ToLogEntry()!;
        }

        return new(Status, ExecutionGasUsed, StateGasUsed, logEntries);
    }
}

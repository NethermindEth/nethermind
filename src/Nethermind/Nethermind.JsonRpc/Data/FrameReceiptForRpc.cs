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

    /// <param name="receipt">The transaction receipt the frame belongs to, which places its logs in the block.</param>
    /// <param name="frameReceipt">The frame's receipt entry.</param>
    /// <param name="blockTimestamp">The timestamp of the including block.</param>
    /// <param name="logIndexStart">The block-global index of the frame's first log.</param>
    public FrameReceiptForRpc(TxReceipt receipt, TxFrameReceipt frameReceipt, ulong blockTimestamp, int logIndexStart)
    {
        Status = frameReceipt.Status;
        ExecutionGasUsed = frameReceipt.ExecutionGasUsed;
        StateGasUsed = frameReceipt.StateGasUsed;
        LogEntry[] logs = frameReceipt.Logs;
        Logs = new LogEntryForRpc[logs.Length];
        for (int i = 0; i < logs.Length; i++)
        {
            Logs[i] = new LogEntryForRpc(receipt, logs[i], blockTimestamp, logIndexStart + i);
        }
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

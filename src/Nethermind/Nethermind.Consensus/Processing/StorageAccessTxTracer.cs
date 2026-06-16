// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// DIAGNOSTIC-ONLY tracer that records the storage cells a transaction reads (SLOAD) and writes
/// (SSTORE), plus the result fingerprint (status, gas used, log count), used to measure the
/// transaction-execution-reuse opportunity during speculative prewarming.
/// </summary>
internal sealed class StorageAccessTxTracer : TxTracer
{
    public override bool IsTracingStorage => true;
    public override bool IsTracingReceipt => true;

    public readonly HashSet<StorageCell> Reads = [];
    public readonly HashSet<StorageCell> Writes = [];

    public bool Completed;
    public byte Status;
    public long GasUsed;
    public int LogsCount;

    public override void LoadOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> value)
        => Reads.Add(new StorageCell(address, in storageIndex));

    public override void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue)
        => Writes.Add(new StorageCell(address, in storageIndex));

    public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
    {
        Completed = true;
        Status = 1;
        GasUsed = gasSpent.SpentGas;
        LogsCount = logs?.Length ?? 0;
    }

    public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
    {
        Completed = true;
        Status = 0;
        GasUsed = gasSpent.SpentGas;
        LogsCount = 0;
    }
}

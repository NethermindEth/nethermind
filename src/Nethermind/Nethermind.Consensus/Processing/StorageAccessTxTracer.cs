// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// DIAGNOSTIC-ONLY tracer that records the storage cells a transaction reads (SLOAD) and writes
/// (SSTORE), used to measure the transaction-execution-reuse opportunity during speculative prewarming.
/// </summary>
internal sealed class StorageAccessTxTracer : TxTracer
{
    public override bool IsTracingStorage => true;

    public readonly HashSet<StorageCell> Reads = [];
    public readonly HashSet<StorageCell> Writes = [];

    public override void LoadOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> value)
        => Reads.Add(new StorageCell(address, in storageIndex));

    public override void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue)
        => Writes.Add(new StorageCell(address, in storageIndex));
}

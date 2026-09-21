// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.State.Flat.History.Changesets;

/// <summary>Records transaction state and storage changes without instruction-level callbacks.</summary>
/// <remarks>Capture enables state/storage tracing commit paths, including their dictionaries, original-value
/// comparisons and slot-value allocations. Inline capture adds that work to block processing.</remarks>
internal sealed class ChangesetTxTracer(ChangesetCollector collector) : TxTracer
{
    public override bool IsTracingState => true;

    public override bool IsTracingStorage => true;

    public override void ReportStorageClear(Address address) => collector.StorageCleared(address);

    public override void ReportStorageRestore(in StorageCell storageCell, byte[] value) => collector.Storage(storageCell, value);

    public override void ReportBalanceChange(Address address, UInt256? before, UInt256? after)
    {
        if (after is { } value) collector.Balance(address, value);
        else collector.Deleted(address);
    }

    public override void ReportNonceChange(Address address, UInt256? before, UInt256? after)
    {
        if (after is { } value) collector.Nonce(address, value);
        else collector.Deleted(address);
    }

    /// <summary>A wipe is not inferred from code appearing over an existing account: every path that clears storage
    /// inside a transaction goes through IWorldState.ClearStorage, and every committed clear is journaled and
    /// reported through ReportStorageClear, so inferring one from a code change would only add a way to disagree.</summary>
    public override void ReportCodeChange(Address address, byte[]? before, byte[]? after)
    {
        if (after is null) collector.Deleted(address);
        else collector.Code(address, after);
    }

    public override void ReportStorageChange(in StorageCell storageCell, byte[] before, byte[] after) =>
        collector.Storage(storageCell, after);
}

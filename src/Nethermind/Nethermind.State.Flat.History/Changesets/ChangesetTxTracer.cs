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

    /// <summary>Code appearing where an account already existed means the account was destroyed and re-created in
    /// this transaction, or created over an account that could only have held storage the create wipes. Either way
    /// its slots read as zero from here on, which no write record can say. Setting or revoking a delegation leaves
    /// storage alone, and a revocation is told apart from a destroy-and-recreate that deploys empty code by what was
    /// there before: a delegation designator, or real code.</summary>
    public override void ReportCodeChange(Address address, byte[]? before, byte[]? after)
    {
        if (after is null)
        {
            collector.Deleted(address);
            return;
        }

        if (before is not null && !Eip7702Constants.IsDelegatedCode(before) && !Eip7702Constants.IsDelegatedCode(after)) collector.StorageCleared(address);
        collector.Code(address, after);
    }

    public override void ReportStorageChange(in StorageCell storageCell, byte[] before, byte[] after) =>
        collector.Storage(storageCell, after);
}

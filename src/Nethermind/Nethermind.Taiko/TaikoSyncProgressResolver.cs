// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Taiko;

/// <remarks>
/// On Taiko, BeaconSync inserts headers via <see cref="IBlockTree.Insert"/> with the
/// <c>BeaconHeaderMetadata</c> flag, which only updates <c>BestSuggestedBeaconHeader</c>
/// — never <c>BestSuggestedHeader</c>. Meanwhile the driver's Engine API forward-fill
/// drives the executor, advancing <c>Head</c> (and the state pointer) past
/// <c>BestSuggestedHeader</c>.
///
/// The default <see cref="SyncProgressResolver"/> reports <c>FindBestHeader</c> from
/// <c>BestSuggestedHeader</c> only, so <see cref="MultiSyncModeSelector.IsSnapshotInvalid"/>
/// fires <c>state &gt; header</c> and <c>processed &gt; block</c>, throws
/// <see cref="System.ComponentModel.InvalidAsynchronousStateException"/>, and the recovery
/// path (<c>BlockTree.LoadBestKnown</c>) cannot find non-beacon headers — so the sync
/// loop wedges and the node stalls behind live tip.
///
/// This decorator widens the two pointers consumed by the snapshot invariant to also
/// consider <c>BestSuggestedBeaconHeader</c> and <c>Head</c>. All other resolver members
/// are forwarded unchanged.
/// </remarks>
public sealed class TaikoSyncProgressResolver(
    IBlockTree blockTree,
    ISyncProgressResolver inner) : ISyncProgressResolver
{
    public long FindBestHeader()
    {
        long suggested = blockTree.BestSuggestedHeader?.Number ?? 0;
        long beaconSuggested = blockTree.BestSuggestedBeaconHeader?.Number ?? 0;
        long head = blockTree.Head?.Number ?? 0;
        return Math.Max(Math.Max(suggested, beaconSuggested), head);
    }

    public long FindBestFullBlock()
    {
        long body = blockTree.BestSuggestedBody?.Number ?? 0;
        long head = blockTree.Head?.Number ?? 0;
        return Math.Max(body, head);
    }

    public long FindBestFullState() => inner.FindBestFullState();
    public bool IsFastBlocksHeadersFinished() => inner.IsFastBlocksHeadersFinished();
    public bool IsFastBlocksBodiesFinished() => inner.IsFastBlocksBodiesFinished();
    public bool IsFastBlocksReceiptsFinished() => inner.IsFastBlocksReceiptsFinished();
    public bool IsLoadingBlocksFromDb() => inner.IsLoadingBlocksFromDb();
    public long FindBestProcessedBlock() => inner.FindBestProcessedBlock();
    public bool IsSnapGetRangesFinished() => inner.IsSnapGetRangesFinished();
    public UInt256 ChainDifficulty => inner.ChainDifficulty;
    public UInt256? GetTotalDifficulty(Hash256 blockHash) => inner.GetTotalDifficulty(blockHash);
    public void RecalculateProgressPointers() => inner.RecalculateProgressPointers();
    public (long BlockNumber, Hash256 BlockHash) SyncPivot => inner.SyncPivot;
}

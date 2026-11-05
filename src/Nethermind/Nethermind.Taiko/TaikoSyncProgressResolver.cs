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
/// The default <see cref="SyncProgressResolver"/> uses <c>BestSuggestedHeader</c> whenever
/// it is present and falls back to <c>Head</c> only when it is absent. On Taiko the stale
/// non-null pointer makes <see cref="MultiSyncModeSelector.IsSnapshotInvalid"/> fire
/// <c>state &gt; header</c> and <c>processed &gt; block</c>, which throws
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
    public ulong FindBestHeader()
    {
        ulong suggested = blockTree.BestSuggestedHeader?.Number ?? 0;
        ulong beaconSuggested = blockTree.BestSuggestedBeaconHeader?.Number ?? 0;
        ulong head = blockTree.Head?.Number ?? 0;
        return Math.Max(Math.Max(suggested, beaconSuggested), head);
    }

    public ulong FindBestFullBlock()
    {
        ulong body = blockTree.BestSuggestedBody?.Number ?? 0;
        ulong head = blockTree.Head?.Number ?? 0;
        return Math.Max(body, head);
    }

    public ulong FindBestFullState() => inner.FindBestFullState();
    public bool IsFastBlocksHeadersFinished() => inner.IsFastBlocksHeadersFinished();
    public bool IsFastBlocksBodiesFinished() => inner.IsFastBlocksBodiesFinished();
    public bool IsFastBlocksReceiptsFinished() => inner.IsFastBlocksReceiptsFinished();
    public bool IsFastBlockAccessListsFinished() => inner.IsFastBlockAccessListsFinished();
    public bool IsLoadingBlocksFromDb() => inner.IsLoadingBlocksFromDb();
    public ulong FindBestProcessedBlock() => inner.FindBestProcessedBlock();
    public UInt256 ChainDifficulty => inner.ChainDifficulty;
    // On Taiko TotalDifficulty is always 0; the default resolver does
    // best.TotalDifficulty - best.Difficulty which underflows UInt256
    // when Difficulty carries the per-block zk-gas value.
    public UInt256? GetTotalDifficulty(Hash256 blockHash) => UInt256.Zero;
    public void RecalculateProgressPointers() => inner.RecalculateProgressPointers();
    public (ulong BlockNumber, Hash256 BlockHash) SyncPivot => inner.SyncPivot;
}

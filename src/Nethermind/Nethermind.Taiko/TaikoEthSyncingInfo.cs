// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Facade.Eth;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Taiko;

/// <remarks>
/// Beacon-sync inserts headers only bump <c>BestSuggestedBeaconHeader</c>, not
/// <c>BestSuggestedHeader</c>. This decorator widens the suggested-header read to
/// <c>max(BestSuggestedHeader, BestSuggestedBeaconHeader)</c> so <c>eth_syncing</c>
/// reports <c>false</c> once <c>Head</c> catches the beacon pivot. FastSync branch
/// is omitted — Taiko's chainspec disables it.
/// </remarks>
public sealed class TaikoEthSyncingInfo(
    IBlockTree blockTree,
    IEthSyncingInfo inner) : IEthSyncingInfo
{
    private const int MaxDistanceForSynced = 8;

    public SyncingResult GetFullInfo()
    {
        long suggestedHeader = blockTree.FindBestSuggestedHeader()?.Number ?? 0;
        long beaconSuggestedHeader = blockTree.BestSuggestedBeaconHeader?.Number ?? 0;
        long bestSuggestedNumber = Math.Max(suggestedHeader, beaconSuggestedHeader);
        long headNumberOrZero = blockTree.Head?.Number ?? 0;
        bool isSyncing = bestSuggestedNumber == 0 || bestSuggestedNumber > headNumberOrZero + MaxDistanceForSynced;

        if (isSyncing)
        {
            return new SyncingResult
            {
                CurrentBlock = headNumberOrZero,
                HighestBlock = bestSuggestedNumber,
                StartingBlock = 0L,
                SyncMode = inner.SyncMode,
                IsSyncing = true
            };
        }

        return SyncingResult.NotSyncing;
    }

    public bool IsSyncing() => GetFullInfo().IsSyncing;
    public TimeSpan UpdateAndGetSyncTime() => inner.UpdateAndGetSyncTime();
    public SyncMode SyncMode => inner.SyncMode;
}

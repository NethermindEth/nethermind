// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Merge.Plugin.GC;

public class NoSyncGcRegionStrategy : IGCStrategy
{
    private readonly ISyncModeSelector _syncModeSelector;
    private readonly bool _canStartNoGCRegion;
    private readonly (int GCGenerationToCollect, int AggressivelyCompactMemory) _gcParams;

    public NoSyncGcRegionStrategy(ISyncModeSelector syncModeSelector, IMergeConfig mergeConfig)
    {
        _syncModeSelector = syncModeSelector;
        _canStartNoGCRegion = mergeConfig.PrioritizeBlockLatency;
        int gcGenerationToCollect = Math.Min(IGCStrategy.Gen2, mergeConfig.GCGenerationToCollect);
        int aggressivelyCompactMemory = Math.Min(IGCStrategy.LOHCompacting, mergeConfig.AggressivelyCompactMemory);
        _gcParams = (gcGenerationToCollect, aggressivelyCompactMemory);
    }

    public bool CanStartNoGCRegion() => _canStartNoGCRegion && _syncModeSelector.Current == SyncMode.WaitingForBlock;
    public (int, int) GetForcedGCParams() => _gcParams;
}

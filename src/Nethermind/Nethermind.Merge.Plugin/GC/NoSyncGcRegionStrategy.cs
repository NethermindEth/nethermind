// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Merge.Plugin.GC;

public class NoSyncGcRegionStrategy : IGCStrategy
{
    private readonly ISyncModeSelector _syncModeSelector;
    private readonly bool _canStartNoGCRegion;
    private readonly (int GCGenerationToCollect, bool AggressivelyCompactMemory) _gcParams;

    public NoSyncGcRegionStrategy(ISyncModeSelector syncModeSelector, IMergeConfig mergeConfig)
    {
        _syncModeSelector = syncModeSelector;
        _canStartNoGCRegion = mergeConfig.PrioritizeBlockLatency;
        _gcParams = (Math.Min(System.GC.MaxGeneration, mergeConfig.GCGenerationToCollect), mergeConfig.AggressivelyCompactMemory);
    }

    public bool CanStartNoGCRegion() =>  _canStartNoGCRegion && _syncModeSelector.Current == SyncMode.WaitingForBlock;
    public (int, bool) GetForcedGCParams() => _gcParams;
}

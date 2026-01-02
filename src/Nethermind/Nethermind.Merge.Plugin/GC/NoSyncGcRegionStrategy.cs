// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Merge.Plugin.GC;

public class NoSyncGcRegionStrategy : IGCStrategy
{
    private readonly ISyncModeSelector _syncModeSelector;
    private readonly bool _canStartNoGCRegion;
    private readonly (GcLevel GCGenerationToCollect, GcCompaction AggressivelyCompactMemory) _gcParams;

    public NoSyncGcRegionStrategy(ISyncModeSelector syncModeSelector, IMergeConfig mergeConfig)
    {
        _syncModeSelector = syncModeSelector;
        _canStartNoGCRegion = mergeConfig.PrioritizeBlockLatency;
        CollectionsPerDecommit = mergeConfig.CollectionsPerDecommit;
        GcLevel gcLevel = (GcLevel)Math.Min((int)GcLevel.Gen2, (int)mergeConfig.SweepMemory);
        GcCompaction gcCompaction = (GcCompaction)Math.Min((int)GcCompaction.Full, (int)mergeConfig.CompactMemory);
        _gcParams = (gcLevel, gcCompaction);
    }

    public int CollectionsPerDecommit { get; }
    public bool CanStartNoGCRegion() => _canStartNoGCRegion && _syncModeSelector.Current == SyncMode.WaitingForBlock;
    public (GcLevel, GcCompaction) GetForcedGCParams() => _gcParams;
}

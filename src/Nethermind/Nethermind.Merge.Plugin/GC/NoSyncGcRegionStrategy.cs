// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Memory;
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
        DecommitMemoryLoadPercent = mergeConfig.DecommitMemoryLoadPercent;
        CollectionsPerUnconditionalDecommit = mergeConfig.CollectionsPerUnconditionalDecommit;
        GcLevel gcLevel = (GcLevel)Math.Min((int)GcLevel.Gen2, (int)mergeConfig.SweepMemory);
        GcCompaction gcCompaction = (GcCompaction)Math.Min((int)GcCompaction.Full, (int)mergeConfig.CompactMemory);
        _gcParams = (gcLevel, gcCompaction);
        PostBlockDelayMs = mergeConfig.PostBlockGcDelayMs ?? (int)((mergeConfig.SecondsPerSlot * 1000) / 8);
        // An explicit delay is kept as set; the slot-derived default only bounds the delay the payload cadence leaves.
        AdaptivePostBlockDelay = mergeConfig.PostBlockGcDelayMs is null;
        NoGcRegionOnNewPayload = mergeConfig.NoGcRegionOnNewPayload;
    }

    public int CollectionsPerDecommit { get; }
    public int DecommitMemoryLoadPercent { get; }
    public int CollectionsPerUnconditionalDecommit { get; }
    public int PostBlockDelayMs { get; }
    public bool AdaptivePostBlockDelay { get; }
    public NoGcRegionMode NoGcRegionOnNewPayload { get; }

    public bool CanStartNoGCRegion() => _canStartNoGCRegion && _syncModeSelector.Current == SyncMode.WaitingForBlock;
    public (GcLevel, GcCompaction) GetForcedGCParams() => _gcParams;
}

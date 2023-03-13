// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Merge.Plugin.GC;

public class NoSyncGcRegionStrategy : IGCStrategy
{
    private readonly ISyncModeSelector _syncModeSelector;
    private readonly IMergeConfig _mergeConfig;

    public NoSyncGcRegionStrategy(ISyncModeSelector syncModeSelector, IMergeConfig mergeConfig)
    {
        _syncModeSelector = syncModeSelector;
        _mergeConfig = mergeConfig;
    }

    public bool ShouldTryToPreventGCDuringBlockProcessing() => _mergeConfig.DisableGCDuringBlockProcessing && _syncModeSelector.Current == SyncMode.WaitingForBlock;
    public int GCGenerationToCollectBetweenBlockProcessing() => _mergeConfig.ForceGCBetweenBLocks;
}

// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Merge.Plugin.GC;

public class NoSyncGcRegionStrategy : IGCStrategy
{
    private readonly ISyncModeSelector _syncModeSelector;

    public NoSyncGcRegionStrategy(ISyncModeSelector syncModeSelector)
    {
        _syncModeSelector = syncModeSelector;
    }

    public bool ShouldControlGCToReducePauses() => _syncModeSelector.Current == SyncMode.WaitingForBlock;
}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.AspNetCore.Routing.Matching;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Synchronization.FastSync;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using System;
using System.Collections.Generic;
using System.Text;

namespace Nethermind.Xdc;

internal class CreateSnapshotOnStateSyncFinished
{
    private ITreeSync _treeSync;
    private readonly IBlockTree _blockTree;
    private readonly ISnapshotManager _snapshotManager;
    private readonly IEpochSwitchManager _epochSwitchManager;
    private readonly ISpecProvider _specProvider;

    public CreateSnapshotOnStateSyncFinished(ITreeSync treeSync, IBlockTree blockTree, ISnapshotManager snapshotManager, IEpochSwitchManager epochSwitchManager, ISpecProvider specProvider)
    {
        _treeSync = treeSync;
        _blockTree = blockTree;
        _snapshotManager = snapshotManager;
        _epochSwitchManager = epochSwitchManager;
        _specProvider = specProvider;
        _treeSync.SyncCompleted += OnSyncCompleteCreateSnapshot;
    }

    private void OnSyncCompleteCreateSnapshot(object? sender, ITreeSync.SyncCompletedEventArgs e)
    {
        _treeSync.SyncCompleted -= OnSyncCompleteCreateSnapshot;

        XdcBlockHeader pivot = (XdcBlockHeader)e.Pivot;

        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(pivot);

        var result = _epochSwitchManager.IsEpochSwitchAtBlock(pivot);

        long gapBlockNum = Math.Max(0, pivot.Number - pivot.Number % spec.EpochLength - spec.Gap);

        BlockHeader gapBlockForPivotEpoch = _blockTree.FindHeader(gapBlockNum);

        Snapshot snapshot = new(gapBlockForPivotEpoch.Number, gapBlockForPivotEpoch.Hash, XdcExtensions.ExtractAddresses(pivot.Validators));
        _snapshotManager.StoreSnapshot(snapshot);

    }
}

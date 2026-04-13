// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using ConcurrentCollections;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.State;
using Nethermind.Synchronization.FastSync;

namespace Nethermind.Xdc;

public class XdcStateSyncPivot(
    IBlockTree blockTree,
    ISyncConfig syncConfig,
    IStateReader stateReader,
    XdcStateSyncSnapshotManager syncSnapshotManager) : IStateSyncPivot
{
    private readonly IBlockTree _blockTree = blockTree;
    private readonly ISyncConfig _syncConfig = syncConfig;
    private readonly IStateReader _stateReader = stateReader;
    private readonly Queue<XdcBlockHeader> _targets = new();
    private XdcBlockHeader? _pivotHeader;
    private bool _initialized;

    private readonly XdcStateSyncSnapshotManager _syncSnapshotManager = syncSnapshotManager;

    public BlockHeader? GetPivotHeader()
    {
        EnsureInitialized();

        while (_targets.Count > 0 && _stateReader.HasStateForBlock(_targets.Peek()))
        {
            XdcBlockHeader completed = _targets.Dequeue();
            _syncSnapshotManager.StoreSnapshot(completed);
        }

        if (_targets.Count > 0)
        {
            return _targets.Peek();
        }

        return _pivotHeader;
    }

    public void UpdateHeaderForcefully() { }
    public ConcurrentHashSet<Hash256> UpdatedStorages { get; } = new();
    public long Diff => (_blockTree.BestSuggestedHeader?.Number ?? 0) - (_pivotHeader?.Number ?? 0);

    private void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;

        long pivotNumber = _syncConfig.PivotNumber;
        if (pivotNumber == 0) return;

        XdcBlockHeader pivotHeader = (XdcBlockHeader)_blockTree.FindHeader(pivotNumber);

        XdcBlockHeader[] gapBlockHeaders = _syncSnapshotManager.GetGapBlocks(pivotHeader);

        foreach (XdcBlockHeader gapBlockHeader in gapBlockHeaders)
        {
            _targets.Enqueue(gapBlockHeader);
        }

        _pivotHeader = pivotHeader;
    }
}

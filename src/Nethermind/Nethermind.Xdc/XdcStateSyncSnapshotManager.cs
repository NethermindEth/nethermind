// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using Nethermind.Xdc.Contracts;
using System.Collections.Generic;

namespace Nethermind.Xdc;

public class XdcStateSyncSnapshotManager
{
    private readonly ISpecProvider _specProvider;
    private readonly IEpochSwitchManager _epochSwitchManager;
    private readonly IBlockTree _blockTree;
    private readonly ISnapshotManager _snapshotManager;
    private readonly IMasternodeVotingContract _masternodeVotingContract;

    public XdcStateSyncSnapshotManager(
        ISpecProvider specProvider,
        IEpochSwitchManager epochSwitchManager,
        IBlockTree blockTree,
        ISnapshotManager snapshotManager,
        IMasternodeVotingContract masternodeVotingContract
    )
    {
        _specProvider = specProvider;
        _epochSwitchManager = epochSwitchManager;
        _blockTree = blockTree;
        _snapshotManager = snapshotManager;
        _masternodeVotingContract = masternodeVotingContract;
    }

    public XdcBlockHeader[] GetGapBlocks(XdcBlockHeader pivotHeader)
    {

        XdcBlockHeader epochSwitchHeader = pivotHeader;

        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(pivotHeader);


        while (!_epochSwitchManager.IsEpochSwitchAtBlock(epochSwitchHeader))
        {
            epochSwitchHeader = (XdcBlockHeader)_blockTree.FindHeader(epochSwitchHeader.ParentHash);
        }

        List<long> gapBlockNumbers = new();

        long gapBlockNum = epochSwitchHeader.Number - epochSwitchHeader.Number % spec.EpochLength - spec.Gap;

        while (gapBlockNum < pivotHeader.Number)
        {
            gapBlockNumbers.Add(gapBlockNum);
            gapBlockNum += spec.EpochLength;
        }

        XdcBlockHeader[] gapBlockHeaders = new XdcBlockHeader[gapBlockNumbers.Count];

        for (int i = 0; i < gapBlockNumbers.Count; i++)
        {
            gapBlockHeaders[i] = (XdcBlockHeader)_blockTree.FindHeader(gapBlockNumbers[i]);
        }

        return gapBlockHeaders;
    }


    public void StoreSnapshot(XdcBlockHeader gapBlockHeader)
    {
        Address[] candidates = _masternodeVotingContract.GetCandidatesByStake(gapBlockHeader);
        Snapshot snapshot = new(gapBlockHeader.Number, gapBlockHeader.Hash, candidates);
        _snapshotManager.StoreSnapshot(snapshot);
    }

    public void StoreSnapshots(XdcBlockHeader pivotHeader)
    {
        foreach (XdcBlockHeader gapBlockHeader in GetGapBlocks(pivotHeader))
        {
            StoreSnapshot(gapBlockHeader);
        }
    }
}

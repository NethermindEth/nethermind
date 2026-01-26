// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using Nethermind.Xdc.Contracts;

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
        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(pivotHeader);

        XdcBlockHeader epochSwitchHeader = pivotHeader;

        while (!_epochSwitchManager.IsEpochSwitchAtBlock(epochSwitchHeader))
        {
            epochSwitchHeader = (XdcBlockHeader)_blockTree.FindHeader(epochSwitchHeader.ParentHash);
        }

        long gapBlockNum = epochSwitchHeader.Number - epochSwitchHeader.Number % spec.EpochLength - spec.Gap;

        int count = (int)((pivotHeader.Number - gapBlockNum) / spec.EpochLength) + 1;
        XdcBlockHeader[] gapBlockHeaders = new XdcBlockHeader[count];

        for (int i = 0; i < count; i++)
        {
            gapBlockHeaders[i] = (XdcBlockHeader)_blockTree.FindHeader(gapBlockNum);
            gapBlockNum += spec.EpochLength;
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

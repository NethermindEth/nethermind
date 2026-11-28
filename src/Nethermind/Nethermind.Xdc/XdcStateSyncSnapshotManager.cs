// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using Nethermind.Xdc.Contracts;
using System;

namespace Nethermind.Xdc;

/// <summary>
/// In XDC, header verification requires snapshots from previous blocks;
/// however, these are not loaded during fast sync because previous headers are not processed normally.
/// This class calculates the required gap block numbers and stores their snapshots.
/// </summary>
public class XdcStateSyncSnapshotManager(
    ISpecProvider specProvider,
    IEpochSwitchManager epochSwitchManager,
    IBlockTree blockTree,
    ISnapshotManager snapshotManager,
    IMasternodeVotingContract masternodeVotingContract
    ) : IXdcStateSyncSnapshotManager
{
    private readonly ISpecProvider _specProvider = specProvider;
    private readonly IEpochSwitchManager _epochSwitchManager = epochSwitchManager;
    private readonly IBlockTree _blockTree = blockTree;
    private readonly ISnapshotManager _snapshotManager = snapshotManager;
    private readonly IMasternodeVotingContract _masternodeVotingContract = masternodeVotingContract;

    public XdcBlockHeader[]? GetGapBlocks(XdcBlockHeader pivotHeader)
    {
        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(pivotHeader);

        if (FindEpochSwitchAtOrBelow(pivotHeader) is not XdcBlockHeader epochSwitchHeader)
            return null;

        // The first checkpoint after the pivot rewards the epoch before the pivot's, so it needs that epoch's snapshot too.
        if (epochSwitchHeader.Number > 0)
        {
            if (_blockTree.FindHeader(epochSwitchHeader.ParentHash) is not XdcBlockHeader parentHeader
                || FindEpochSwitchAtOrBelow(parentHeader) is not XdcBlockHeader previousEpochSwitchHeader)
                return null;

            epochSwitchHeader = previousEpochSwitchHeader;
        }

        ulong gapBlockNum = Math.Max(
            epochSwitchHeader.Number - epochSwitchHeader.Number % spec.EpochLength,
            spec.EpochLength
         ) - spec.Gap;

        if (gapBlockNum + spec.Gap == spec.SwitchBlock)
        {
            if (_blockTree.FindHeader(spec.SwitchBlock) is not XdcBlockHeader checkpointHeader
                || _blockTree.FindHeader(gapBlockNum) is not XdcBlockHeader gapBlockHeader)
                return null;

            Snapshot snapshot = new(gapBlockHeader.Number, gapBlockHeader.Hash, checkpointHeader.ExtraData.ParseV1Masternodes());
            _snapshotManager.StoreSnapshot(snapshot);

            gapBlockNum += spec.EpochLength;
        }

        if (gapBlockNum > pivotHeader.Number)
        {
            return [];
        }

        int count = (int)((pivotHeader.Number - gapBlockNum) / spec.EpochLength) + 1;
        XdcBlockHeader[] gapBlockHeaders = new XdcBlockHeader[count];

        for (int i = 0; i < count; i++)
        {
            if (_blockTree.FindHeader(gapBlockNum) is not XdcBlockHeader gapBlockHeader)
                return null;

            gapBlockHeaders[i] = gapBlockHeader;
            gapBlockNum += spec.EpochLength;
        }

        return gapBlockHeaders;
    }

    private XdcBlockHeader? FindEpochSwitchAtOrBelow(XdcBlockHeader header)
    {
        while (header.Number > 0 && !_epochSwitchManager.IsEpochSwitchAtBlock(header))
        {
            if (_blockTree.FindHeader(header.ParentHash) is not XdcBlockHeader parentHeader)
                return null;

            header = parentHeader;
        }

        return header;
    }


    public void StoreSnapshot(XdcBlockHeader gapBlockHeader)
    {
        Address[] candidates = _masternodeVotingContract.GetCandidatesByStake(gapBlockHeader);
        Snapshot snapshot = new(gapBlockHeader.Number, gapBlockHeader.Hash, candidates);
        _snapshotManager.StoreSnapshot(snapshot);
    }
}

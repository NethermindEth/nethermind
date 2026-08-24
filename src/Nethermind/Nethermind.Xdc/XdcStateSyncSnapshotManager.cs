// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Extensions;
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

        XdcBlockHeader epochSwitchHeader = pivotHeader;

        while (!_epochSwitchManager.IsEpochSwitchAtBlock(epochSwitchHeader))
        {
            if (_blockTree.FindHeader(epochSwitchHeader.ParentHash) is not XdcBlockHeader parentHeader)
                return null;

            epochSwitchHeader = parentHeader;
        }

        ulong epochBase = Math.Max(
            epochSwitchHeader.Number - epochSwitchHeader.Number % spec.EpochLength,
            spec.EpochLength
         );

        // The penalty comeback check at an epoch switch reaches LimitPenaltyEpoch epochs back
        // (see PenaltyHandler.HandlePenalties), and resolving an epoch needs its gap block snapshot.
        // Nothing below the pivot is ever processed, so those snapshots only exist if built from synced state here.
        ulong switchBlockEpochBase = spec.SwitchBlock - spec.SwitchBlock % spec.EpochLength;
        ulong lookbackFloor = Math.Min(epochBase, Math.Max(switchBlockEpochBase, spec.EpochLength));
        ulong gapBlockNum = Math.Max(epochBase.SaturatingSub(XdcConstants.LimitPenaltyEpoch * spec.EpochLength), lookbackFloor) - spec.Gap;

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


    public void StoreSnapshot(XdcBlockHeader gapBlockHeader)
    {
        Address[] candidates = _masternodeVotingContract.GetCandidatesByStake(gapBlockHeader);
        Snapshot snapshot = new(gapBlockHeader.Number, gapBlockHeader.Hash, candidates);
        _snapshotManager.StoreSnapshot(snapshot);
    }
}

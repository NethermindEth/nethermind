// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Xdc.Contracts;
using Nethermind.Xdc.RLP;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc;

internal class SubnetSnapshotManager : BaseSnapshotManager<SubnetSnapshot>
{
    private IPenaltyHandler PenaltyHandler { get; }
    public SubnetSnapshotManager(IDb snapshotDb, IBlockTree blockTree, IPenaltyHandler penaltyHandler, IMasternodeVotingContract votingContract, ISpecProvider specProvider)
        : base(snapshotDb, blockTree, votingContract, specProvider, new SubnetSnapshotDecoder(), "XDC Subnet Snapshot cache")
    {
        PenaltyHandler = penaltyHandler;
    }

    protected override SubnetSnapshot CreateSnapshot(XdcBlockHeader header, IXdcReleaseSpec spec)
    {
        Address[] candidates = header.IsGenesis ? spec.GenesisMasterNodes : VotingContract.GetCandidatesByStake(header);
        Address[] penalties = PenaltyHandler.HandlePenalties(header.Number, header.ParentHash, candidates);

        return new SubnetSnapshot(header.Number, header.Hash, candidates, penalties);
    }
}

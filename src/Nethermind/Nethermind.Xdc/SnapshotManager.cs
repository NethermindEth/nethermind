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

internal class SnapshotManager : BaseSnapshotManager<Snapshot>
{
    public SnapshotManager(IDb snapshotDb, IBlockTree blockTree, IPenaltyHandler penaltyHandler, IMasternodeVotingContract votingContract, ISpecProvider specProvider)
        : base(snapshotDb, blockTree, penaltyHandler, votingContract, specProvider, new SnapshotDecoder(), "XDC Snapshot cache")
    {
    }

    protected override Snapshot CreateSnapshot(XdcBlockHeader header, IXdcReleaseSpec spec)
    {
        Address[] candidates = header.IsGenesis ? spec.GenesisMasterNodes : VotingContract.GetCandidatesByStake(header);

        return new Snapshot(header.Number, header.Hash, candidates);
    }
}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
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

internal sealed class SubnetSnapshotManager(
    IDb snapshotDb,
    IBlockTree blockTree,
    IMasternodeVotingContract votingContract,
    ISpecProvider specProvider,
    IPenaltyHandler penaltyHandler)
    : BaseSnapshotManager<SubnetSnapshot>(
        snapshotDb,
        blockTree,
        votingContract,
        specProvider,
        new SubnetSnapshotDecoder(),
        cacheName: "XDC Subnet Snapshot cache")
{
    protected override SubnetSnapshot CreateSnapshot(XdcBlockHeader header, IXdcReleaseSpec spec)
    {
        Address[] candidates = header.IsGenesis ? spec.GenesisMasterNodes : VotingContract.GetCandidatesByStake(header);
        Address[] penalties = header.IsGenesis ? [] : penaltyHandler.HandlePenalties(header.Number, header.ParentHash!, candidates);

        return new SubnetSnapshot(header.Number, header.Hash!, candidates, penalties);
    }
}

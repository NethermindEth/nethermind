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

internal sealed class SnapshotManager(
    IDb snapshotDb,
    IBlockTree blockTree,
    IMasternodeVotingContract votingContract,
    ISpecProvider specProvider)
    : BaseSnapshotManager<Snapshot>(
        snapshotDb,
        blockTree,
        votingContract,
        specProvider,
        new SnapshotDecoder(),
        cacheName: "XDC Snapshot cache")
{
    protected override Snapshot CreateSnapshot(XdcBlockHeader header, IXdcReleaseSpec spec)
    {
        Address[] candidates = header.IsGenesis ? spec.GenesisMasterNodes : VotingContract.GetCandidatesByStake(header);

        return new Snapshot(header.Number, header.Hash!, candidates);
    }
}

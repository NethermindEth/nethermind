// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Xdc.Contracts;
using Nethermind.Xdc.RLP;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using System;
using System.Linq;

namespace Nethermind.Xdc;

internal class SubnetSnapshotManager : BaseSnapshotManager<SubnetSnapshot>
{
    public SubnetSnapshotManager(IDb snapshotDb, IBlockTree blockTree, IPenaltyHandler penaltyHandler, IMasternodeVotingContract votingContract, ISpecProvider specProvider)
        : base(snapshotDb, blockTree, penaltyHandler, votingContract, specProvider, new SubnetSnapshotDecoder(), "XDC Subnet Snapshot cache")
    {
    }

    public override (Address[] Masternodes, Address[] PenalizedNodes) CalculateNextEpochMasternodes(long blockNumber, Hash256 parentHash, IXdcReleaseSpec spec)
    {
        int maxMasternodes = spec.MaxMasternodes;
        SubnetSnapshot? previousSnapshot = GetSnapshotByBlockNumber(blockNumber, spec);

        if (previousSnapshot is null)
            throw new InvalidOperationException($"No snapshot found for header #{blockNumber}");

        Address[] candidates = previousSnapshot.NextEpochCandidates;
        Address[] penalties = previousSnapshot.NextEpochPenalties;

        candidates = candidates
            .Except(penalties)        // remove penalties
            .Take(maxMasternodes)     // enforce max cap
            .ToArray();

        return (candidates, penalties);
    }

    protected override SubnetSnapshot CreateSnapshot(XdcBlockHeader header, Address[] candidates, IXdcReleaseSpec spec)
    {
        Address[] penalties = PenaltyHandler.HandlePenalties(header.Number, header.ParentHash, candidates);
        return new SubnetSnapshot(header.Number, header.Hash, candidates, penalties);
    }
}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using System;
using System.Linq;

namespace Nethermind.Xdc;

internal class SubnetMasternodesCalculator(ISubnetSnapshotManager snapshotManager) : ISubnetMasternodesCalculator
{
    public (Address[] Masternodes, Address[] PenalizedNodes) CalculateNextEpochMasternodes(long blockNumber, Hash256 parentHash, IXdcReleaseSpec spec)
    {
        SubnetSnapshot previousSnapshot = (SubnetSnapshot)snapshotManager.GetSnapshotByBlockNumber(blockNumber, spec);

        if (previousSnapshot is null)
            throw new InvalidOperationException($"No snapshot found for header #{blockNumber}");

        return (previousSnapshot.NextEpochCandidates
            .Except(previousSnapshot.NextEpochPenalties)
            .Take(spec.MaxMasternodes)
            .ToArray(), previousSnapshot.NextEpochPenalties);
    }

    public (Address[] nextEpochCandidates, Address[] nextPenalties) GetNextEpochCandidatesAndPenalties(Hash256 parentHash)
    {
        SubnetSnapshot snapshot = snapshotManager.GetSnapshotByHash(parentHash);
        return (snapshot.NextEpochCandidates, snapshot.NextEpochPenalties);
    }
}

public interface ISubnetMasternodesCalculator : IMasternodesCalculator
{
    (Address[] nextEpochCandidates, Address[] nextPenalties) GetNextEpochCandidatesAndPenalties(Hash256 parentHash);
}

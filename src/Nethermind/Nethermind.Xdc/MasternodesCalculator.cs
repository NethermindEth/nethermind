// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Nethermind.Xdc;

internal class MasternodesCalculator(ISnapshotManager snapshotManager, IPenaltyHandler penaltyHandler) : IMasternodesCalculator
{
    public (Address[] Masternodes, Address[] PenalizedNodes) CalculateNextEpochMasternodes(long blockNumber, Hash256 parentHash, IXdcReleaseSpec spec)
    {
        int maxMasternodes = spec.MaxMasternodes;
        Snapshot previousSnapshot = snapshotManager.GetSnapshotByBlockNumber(blockNumber, spec);

        if (previousSnapshot is null)
            throw new InvalidOperationException($"No snapshot found for header #{blockNumber}");

        Address[] candidates = previousSnapshot.NextEpochCandidates;

        if (blockNumber == spec.SwitchBlock + 1)
        {
            if (candidates.Length > maxMasternodes)
            {
                Array.Resize(ref candidates, maxMasternodes);
                return (candidates, []);
            }

            return (candidates, []);
        }

        Address[] penalties = penaltyHandler.HandlePenalties(blockNumber, parentHash, candidates);

        candidates = candidates
            .Except(penalties)        // remove penalties
            .Take(maxMasternodes)     // enforce max cap
            .ToArray();

        return (candidates, penalties);
    }
}

// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Xdc.Contracts;
using System.Linq;

namespace Nethermind.Xdc.Test.Helpers;

internal class XdcTestDepositContract(CandidateContainer candidateContainer) : IMasternodeVotingContract
{
    public Address[] GetCandidatesByStake(BlockHeader blockHeader)
    {
        //We fake ordering by returning addresses instead of stake in descending order
        return candidateContainer.MasternodeCandidates.Select(m => m.Address).OrderByDescending(a => a).ToArray();
    }

    public Address[] GetCandidates(BlockHeader blockHeader)
    {
        return candidateContainer.MasternodeCandidates.Select(m => m.Address).ToArray();
    }

    public UInt256 GetCandidateStake(BlockHeader blockHeader, Address candidate)
    {
        return 10_000_000.Ether();
    }
}

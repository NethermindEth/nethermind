// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Shuffling;

namespace Nethermind.BeaconChain.Api.Common;

/// <summary>Resolves the three queryable committee epochs through the node's shared cache.</summary>
internal sealed class StateCommitteeCache(CommitteeCacheLru committees)
{
    internal CommitteeCache Get(ApiState state, ulong epoch)
    {
        ulong currentEpoch = BeaconStateAccessors.ComputeEpochAtSlot(state.Slot);
        if (epoch != currentEpoch && !(currentEpoch > 0 && epoch == currentEpoch - 1) && epoch != currentEpoch + 1)
            throw new ArgumentOutOfRangeException(nameof(epoch));
        return state.GetCommittees(committees, epoch);
    }
}

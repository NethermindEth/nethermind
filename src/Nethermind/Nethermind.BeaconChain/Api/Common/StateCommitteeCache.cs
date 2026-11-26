// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Shuffling;

namespace Nethermind.BeaconChain.Api.Common;

/// <summary>Caches the three queryable committee epochs of immutable API state snapshots.</summary>
internal sealed class StateCommitteeCache
{
    private readonly ConditionalWeakTable<object, Lazy<CommitteeCache>?[]> _states = [];

    internal CommitteeCache Get(ApiState state, ulong epoch)
    {
        ulong currentEpoch = BeaconStateAccessors.ComputeEpochAtSlot(state.Slot);
        int index = epoch == currentEpoch ? 1
            : currentEpoch > 0 && epoch == currentEpoch - 1 ? 0
            : epoch == currentEpoch + 1 ? 2
            : throw new ArgumentOutOfRangeException(nameof(epoch));
        object identity = (object?)state.Electra ?? state.Gloas!;
        Lazy<CommitteeCache>?[] epochs = _states.GetValue(identity, static _ => new Lazy<CommitteeCache>?[3]);
        Lazy<CommitteeCache>? entry = Volatile.Read(ref epochs[index]);
        if (entry is null)
        {
            Lazy<CommitteeCache> candidate = new(() => state.BuildCommittees(epoch));
            entry = Interlocked.CompareExchange(ref epochs[index], candidate, null) ?? candidate;
        }
        return entry.Value;
    }
}

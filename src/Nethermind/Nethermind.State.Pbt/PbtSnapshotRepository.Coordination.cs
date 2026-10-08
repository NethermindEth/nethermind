// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Collections;
using Nethermind.State.Flat;

namespace Nethermind.State.Pbt;

public partial class PbtSnapshotRepository
{
    internal bool CanReachState(in StateId head, in StateId target)
    {
        if (head == target) return true;
        StateId wanted = target;
        using PbtSnapshotChain? chain = Walk(head, ReadPriority, edge =>
            edge.From == wanted ? Step.Stop : Height(edge.From) > Height(wanted) ? Step.Traverse : Step.Skip);
        return chain is not null;
    }

    private static readonly ValueHash256 MaximumRetainedRoot = CreateMaximumRoot();

    private static ValueHash256 CreateMaximumRoot()
    {
        Span<byte> bytes = stackalloc byte[32];
        bytes.Fill(byte.MaxValue);
        return new(bytes);
    }

    internal ArrayPoolList<StateId> GetRetainedStatesInRange(ulong first, ulong last)
    {
        ArrayPoolList<StateId> states = new(16);
        lock (_lock)
            foreach (StateId state in _retainedStateIds.GetViewBetween(new(first, default), new(last, MaximumRetainedRoot)))
                states.Add(state);
        return states;
    }

    private void RemoveRetainedState(in StateId state)
    {
        List<(long Depth, SnapshotTier Tier)> entries = [];
        lock (_lock)
            foreach (SnapshotTier tier in RetainedPriority)
                if (_retainedEdges.TryGetValue((state, tier), out SortedDictionary<long, PersistedSnapshots.PbtRetainedSnapshot>? edges))
                    foreach (long depth in edges.Keys) entries.Add((depth, tier));
        foreach ((long depth, SnapshotTier tier) in entries) RemoveRetainedExact(state, depth, tier);
    }

    internal void RemoveFinalizedRetainedForks(in StateId persisted, IStateHeaderProvider provider)
    {
        lock (_publicationGate.Sync)
        {
            StateId? committed = GetLastCommittedStateId();
            ulong finalized = provider.FinalizedBlockNumber;
            if (committed is null || committed == StateId.PreGenesis || finalized > committed.Value.BlockNumber) return;
            StateId head = committed.Value;
            bool ancestryVerified = false;
            ulong first = persisted == StateId.PreGenesis ? 0 : persisted.BlockNumber;
            const ulong batchSize = 4096;
            while (first <= finalized)
            {
                ulong last = finalized - first >= batchSize - 1 ? first + batchSize - 1 : finalized;
                using ArrayPoolList<StateId> states = GetRetainedStatesInRange(first, last);
                foreach (StateId state in states)
                {
                    if (state == persisted) continue;
                    Hash256? root = provider.GetFinalizedHeader(state.BlockNumber)?.StateRoot;
                    if (root is null || state.StateRoot == root.ValueHash256) continue;
                    if (!ancestryVerified)
                    {
                        Hash256? anchor = provider.GetFinalizedHeader(finalized)?.StateRoot;
                        if (anchor is not null && !CanReachState(head, new StateId(finalized, anchor))) return;
                        if (persisted != StateId.PreGenesis && !CanReachState(head, persisted)) return;
                        ancestryVerified = true;
                    }
                    if (GetLastCommittedStateId() != committed) return;
                    if (CanReachState(head, state)) continue;
                    RemoveRetainedState(state);
                }
                if (last == finalized) break;
                first = last + 1;
            }
        }
    }
}

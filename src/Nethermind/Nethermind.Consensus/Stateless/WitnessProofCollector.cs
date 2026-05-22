// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Collections.Pooled;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.State;
using Nethermind.State.Proofs;

namespace Nethermind.Consensus.Stateless;

/// <summary>
/// Shared helpers for assembling Merkle-proof state nodes from a per-address slot map.
/// Used by both <see cref="WitnessGeneratingWorldState"/> (post-hoc) and
/// <see cref="WitnessCapturingWorldStateProxy"/> (in-flight).
/// </summary>
internal static class WitnessProofCollector
{
    /// <summary>
    /// For each (address, slots) entry runs an <see cref="AccountProofCollector"/> tree
    /// visit against <paramref name="parentHeader"/> and appends every node from the
    /// account proof + storage proofs into <paramref name="stateNodes"/>.
    /// </summary>
    public static void CollectAccountProofs(
        IReadOnlyDictionary<Address, HashSet<UInt256>> storageSlots,
        IStateReader stateReader,
        BlockHeader parentHeader,
        PooledSet<byte[]> stateNodes)
    {
        foreach ((Address account, HashSet<UInt256> slots) in storageSlots)
        {
            AccountProofCollector collector = new(account, slots);
            stateReader.RunTreeVisitor(collector, parentHeader);
            (IReadOnlyList<byte[]> accountProof, IReadOnlyList<byte[]>[] storageProof) = collector.GetRawResult();
            AddRange(stateNodes, accountProof);
            foreach (IReadOnlyList<byte[]> storage in storageProof)
                AddRange(stateNodes, storage);
        }
    }

    private static void AddRange(PooledSet<byte[]> set, IReadOnlyList<byte[]> items)
    {
        for (int i = 0; i < items.Count; i++)
            set.Add(items[i]);
    }
}

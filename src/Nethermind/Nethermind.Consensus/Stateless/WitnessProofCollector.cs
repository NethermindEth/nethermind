// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Collections.Pooled;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.State;
using Nethermind.State.Proofs;

namespace Nethermind.Consensus.Stateless;

/// <summary>Shared per-address proof collection used by both the post-hoc and in-flight witness paths.</summary>
internal static class WitnessProofCollector
{
    /// <summary>Runs <see cref="AccountProofCollector"/> for each (address, slots) entry and aggregates the nodes.</summary>
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

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

/// <summary>
/// Merges a chain of consecutive diff layers into a single layer covering the whole segment,
/// applied newest-wins so the result is what a bundle walk over the originals would observe.
/// </summary>
/// <remarks>
/// The merged layer shares its blob and node arrays with the layers it merged, so it must not outlive
/// them unless it takes leases of its own.
/// </remarks>
public class PbtSnapshotCompactor(IPbtResourcePool resourcePool)
{
    /// <param name="chainOldestFirst">Consecutive snapshots, oldest first, as produced by <see cref="PbtSnapshotRepository.TryLeaseChain"/>.</param>
    public PbtSnapshot Compact(IReadOnlyList<PbtSnapshot> chainOldestFirst)
    {
        // sized by what is actually merged: a segment is also persisted one layer deep on a genesis
        // flush and up to the reorg depth by the finality-stall backstop, so the configured compact
        // size would mis-charge the pool on both
        PbtResourcePool.Usage usage = PbtResourcePool.CompactUsage(chainOldestFirst.Count);
        PbtSnapshotContent merged = resourcePool.GetSnapshotContent(usage);

        // First pass: the newest layer that self-destructs each address. A slot written strictly
        // before that layer is wiped by the destruct, so it is filtered out of the merge below rather
        // than added and then hunted down — which is what the whole segment's slots used to be
        // re-scanned for, once per self-destruct in every layer.
        Dictionary<AddressAsKey, int> slotClearBoundary = [];
        for (int i = 0; i < chainOldestFirst.Count; i++)
        {
            foreach ((AddressAsKey address, _) in chainOldestFirst[i].Content.SelfDestructs)
            {
                slotClearBoundary[address] = i;
            }
        }

        // Second pass, oldest to newest, so a later layer's write overwrites an earlier one:
        // reversing this inverts precedence and writes stale values to disk without any error.
        for (int i = 0; i < chainOldestFirst.Count; i++)
        {
            PbtSnapshotContent content = chainOldestFirst[i].Content;

            foreach ((AddressAsKey address, Account? account) in content.Accounts)
            {
                merged.Accounts[address] = account;
            }

            // the markers are kept so persistence still range-deletes the on-disk storage
            foreach ((AddressAsKey address, _) in content.SelfDestructs)
            {
                merged.SelfDestructs[address] = true;
            }

            foreach (((AddressAsKey Address, UInt256 Slot) slotKey, EvmWord value) in content.Slots)
            {
                // a slot written in the destructing layer itself is a post-destruct write and survives
                if (slotClearBoundary.TryGetValue(slotKey.Address, out int boundary) && i < boundary) continue;
                merged.Slots[slotKey] = value;
            }

            foreach ((Stem stem, byte[] blob) in content.LeafBlobs)
            {
                merged.LeafBlobs[stem] = blob;
            }

            foreach ((TrieNodeKey key, byte[]? node) in content.TrieNodes)
            {
                merged.TrieNodes[key] = node;
            }
        }

        return new PbtSnapshot(chainOldestFirst[0].From, chainOldestFirst[^1].To, merged, resourcePool, usage);
    }
}

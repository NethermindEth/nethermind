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
public static class PbtSnapshotCompactor
{
    /// <param name="chainNewestFirst">Consecutive snapshots, newest first, as produced by <see cref="PbtSnapshotRepository.TryLeaseChain"/>.</param>
    public static PbtSnapshot Compact(IReadOnlyList<PbtSnapshot> chainNewestFirst)
    {
        PbtSnapshotContent merged = new();
        for (int i = chainNewestFirst.Count - 1; i >= 0; i--)
        {
            PbtSnapshotContent content = chainNewestFirst[i].Content;

            foreach ((AddressAsKey address, Account? account) in content.Accounts)
            {
                merged.Accounts[address] = account;
            }

            // a newer self-destruct hides every older slot write for the address; the marker itself
            // is kept so persistence still range-deletes the on-disk storage
            foreach ((AddressAsKey address, _) in content.SelfDestructs)
            {
                merged.SelfDestructs[address] = true;
                foreach (((AddressAsKey Address, UInt256 Slot) slotKey, _) in merged.Slots)
                {
                    if (slotKey.Address.Equals(address)) merged.Slots.TryRemove(slotKey, out _);
                }
            }

            foreach (((AddressAsKey, UInt256) slotKey, byte[]? value) in content.Slots)
            {
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

        return new PbtSnapshot(chainNewestFirst[^1].From, chainNewestFirst[0].To, merged);
    }
}

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
public class PbtSnapshotCompactor(IPbtResourcePool resourcePool, PbtCompactionSchedule schedule, PbtSnapshotRepository repository, IPbtConfig config)
{
    private readonly ulong _fullCompactSize = (ulong)config.CompactSize;

    /// <summary>Runs the compaction the schedule calls for at <paramref name="stateId"/>, if any.</summary>
    /// <returns>Whether a compacted layer was published.</returns>
    /// <remarks>
    /// Called for every committed block; the schedule decides that most of them merge nothing. The
    /// window's inputs stay in the repository afterwards: the compacted layer is a shortcut across
    /// them, and a walk aiming between the wide boundaries still needs the narrow ones.
    /// </remarks>
    public bool DoCompactSnapshot(in StateId stateId)
    {
        ulong width = schedule.GetCompactSize(stateId.BlockNumber);
        if (width <= 1) return false;

        // the window starts a full width back, which may be below genesis for an early wide boundary —
        // signed, so that fails to assemble rather than wrapping into an enormous window
        long start = (long)stateId.BlockNumber - (long)width;
        using PbtSnapshotPooledList window = new((int)width);
        if (!repository.TryLeaseCompactionWindow(stateId, start, window) || window.Count <= 1) return false;

        PbtSnapshot compacted = Compact(window);
        if (!repository.TryAddCompacted(compacted)) return false;

        // Anything narrower than a full window is itself about to be spanned by a wider one, so the
        // compacted layer a full width back is now subsumed and only costs memory. The full-width
        // layers are the persistence boundaries and are left for the coordinator to consume.
        if (width < _fullCompactSize && stateId.BlockNumber >= _fullCompactSize)
        {
            repository.RemoveCompactedAt(stateId.BlockNumber - _fullCompactSize);
        }

        return true;
    }

    /// <param name="chainOldestFirst">Consecutive snapshots, oldest first, as produced by <see cref="PbtSnapshotRepository.TryLeaseChain"/>.</param>
    public PbtSnapshot Compact(IReadOnlyList<PbtSnapshot> chainOldestFirst)
    {
        // sized by what is actually merged: a segment is also persisted one layer deep on a genesis
        // flush and up to the reorg depth by the finality-stall backstop, so the configured compact
        // size would mis-charge the pool on both
        PbtResourcePool.Usage usage = PbtResourcePool.CompactUsage(chainOldestFirst.Count);
        PbtSnapshotContent merged = resourcePool.GetSnapshotContent(usage);

        // First pass: the newest layer that self-destructs each address. A slot written strictly
        // before that layer is wiped by the destruct, so it is filtered out of the merge below.
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

        PbtSnapshot newest = chainOldestFirst[^1];
        return new PbtSnapshot(chainOldestFirst[0].From, newest.To, newest.TreeRoot, merged, resourcePool, usage);
    }
}

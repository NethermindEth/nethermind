// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

/// <summary>
/// Merges a chain of consecutive diff layers into a single layer covering the whole segment,
/// applied newest-wins so the result is what a bundle walk over the originals would observe.
/// </summary>
/// <remarks>
/// The merged layer takes an independent lease on every non-null value it shares with its inputs, so
/// the merged and source layers may be disposed in either order.
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

        // A signed start lets early boundaries below genesis fail instead of wrapping.
        long start = (long)stateId.BlockNumber - (long)width;
        using PbtSnapshotPooledList window = new((int)width);
        if (!repository.TryLeaseCompactionWindow(stateId, start, window) || window.Count <= 1) return false;

        PbtSnapshot compacted = Compact(window);
        if (!repository.TryAddCompacted(compacted)) return false;

        // A wider compaction subsumes older narrow layers; full-width layers remain persistence boundaries.
        if (width < _fullCompactSize && stateId.BlockNumber >= _fullCompactSize)
        {
            repository.RemoveCompactedAt(stateId.BlockNumber - _fullCompactSize);
        }

        return true;
    }

    /// <param name="chainOldestFirst">Consecutive snapshots, oldest first, as produced by <see cref="PbtSnapshotRepository.TryLeaseChain"/>.</param>
    public PbtSnapshot Compact(IReadOnlyList<PbtSnapshot> chainOldestFirst)
    {
        // Genesis flushes and backstop persistence may merge less than the configured compact size.
        PbtResourcePool.Usage usage = PbtResourcePool.CompactUsage(chainOldestFirst.Count);
        PbtSnapshotContent merged = resourcePool.GetSnapshotContent(usage);

        try
        {
            // Oldest to newest, so a later layer's write overwrites an earlier one: reversing this inverts
            // precedence and writes stale values to disk without any error.
            for (int i = 0; i < chainOldestFirst.Count; i++)
            {
                PbtSnapshotContent content = chainOldestFirst[i].Content;

                foreach ((PbtFullKey key, ValueHash256? value) in content.FullLeaves)
                {
                    merged.SetFullLeaf(key, value);
                }

                foreach (PbtSnapshotContent.Partition partition in content.Partitions)
                {
                    foreach ((Stem stem, RefCountingMemory? blob) in partition.LeafBlobs)
                    {
                        blob?.AcquireLease();
                        merged.SetLeafBlob(stem, blob);
                    }

                    foreach ((TrieNodeKey key, RefCountingMemory? node) in partition.TrieNodes)
                    {
                        node?.AcquireLease();
                        merged.SetTrieNode(key, node);
                    }
                }
            }

            PbtSnapshot newest = chainOldestFirst[^1];
            return new PbtSnapshot(chainOldestFirst[0].From, newest.To, newest.PartitionRoots, merged, resourcePool, usage);
        }
        catch
        {
            resourcePool.ReturnSnapshotContent(usage, merged);
            throw;
        }
    }
}

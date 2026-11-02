// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat;

namespace Nethermind.State.Pbt.Snapshot;

/// <summary>Merges consecutive canonical snapshot diffs without changing newest-write precedence.</summary>
public class PbtSnapshotCompactor(
    IPbtResourcePool resourcePool,
    [KeyFilter(DbNames.Pbt)] ICompactionSchedule schedule,
    PbtSnapshotRepository repository,
    IPbtConfig config,
    ILogManager logManager)
{
    private readonly ILogger _logger = logManager.GetClassLogger<PbtSnapshotCompactor>();

    public bool DoCompactSnapshot(in StateId stateId)
    {
        if (repository.TryLeaseMemoryState(stateId, SnapshotTier.InMemoryBase, out PbtSnapshot? snapshot))
        {
            using (snapshot) snapshot!.PopulateBloom();
        }

        ulong width = schedule.GetCompactSize(stateId.BlockNumber);
        if (width <= 1) return false;
        if (width < (ulong)config.CompactSize && stateId.BlockNumber >= (ulong)config.CompactSize)
            repository.RemoveCompactedAt(stateId.BlockNumber - (ulong)config.CompactSize);
        long floor = checked((long)stateId.BlockNumber - (long)width);
        using PbtSnapshotPooledList chain = repository.LeaseCompactionWindow(stateId, floor);
        if (chain.Count == 0) return false;
        if (_logger.IsDebug) _logger.Debug($"Compacting Pbt snapshots {chain[0].From} -> {stateId}: chainCount={chain.Count}, snapshots={repository.Count}, compactedSnapshots={repository.CompactedCount}, managedBytes={GC.GetTotalMemory(false)}");
        PbtSnapshot compacted = Compact(chain, PbtResourcePool.CompactUsage((int)width));
        bool added = repository.TryAddCompacted(compacted);
        if (_logger.IsDebug) _logger.Debug($"Completed Pbt compaction up to {stateId}: added={added}, snapshots={repository.Count}, compactedSnapshots={repository.CompactedCount}, managedBytes={GC.GetTotalMemory(false)}");
        return added;
    }

    private PbtSnapshot Compact(IReadOnlyList<PbtSnapshot> chainOldestFirst, PbtResourcePool.Usage usage)
    {
        PbtSortedSnapshotContent merged = resourcePool.GetSortedSnapshotContent(usage);
        try
        {
            merged.BuildFromMerge(chainOldestFirst);
        }
        catch
        {
            resourcePool.ReturnSortedSnapshotContent(usage, merged); // don't leak the pooled buffers on failure
            throw;
        }

        PbtSnapshot newest = chainOldestFirst[^1];
        return new PbtSnapshot(chainOldestFirst[0].From, newest.To, newest.TreeRoot, null, merged, resourcePool, usage);
    }
}

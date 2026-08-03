// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

/// <summary>Merges consecutive canonical snapshot diffs without changing newest-write precedence.</summary>
public class PbtSnapshotCompactor(
    IPbtResourcePool resourcePool,
    PbtCompactionSchedule schedule,
    PbtSnapshotRepository repository,
    IPbtConfig config)
{
    public bool DoCompactSnapshot(in StateId stateId)
    {
        ulong width = schedule.GetCompactSize(stateId.BlockNumber);
        if (width <= 1) return false;
        using PbtSnapshotPooledList chain = new((int)width);
        long floor = checked((long)stateId.BlockNumber - (long)width);
        if (!repository.TryLeaseCompactionWindow(stateId, floor, chain)) return false;
        PbtSnapshot compacted = Compact(chain);
        if (!repository.TryAddCompacted(compacted)) return false;
        if (width >= (ulong)config.CompactSize) repository.RemoveCompactedAt(stateId.BlockNumber - width);
        return true;
    }

    public PbtSnapshot Compact(IReadOnlyList<PbtSnapshot> chainOldestFirst)
    {
        PbtResourcePool.Usage usage = PbtResourcePool.CompactUsage(chainOldestFirst.Count);
        PbtSnapshotContent merged = resourcePool.GetSnapshotContent(usage);
        try
        {
            for (int i = 0; i < chainOldestFirst.Count; i++)
            {
                PbtSnapshotContent content = chainOldestFirst[i].Content;
                foreach ((PbtFullKey key, ValueHash256? value) in content.Leaves) merged.SetLeaf(key, value);
                foreach ((PbtFullKey locator, byte[]? node) in content.Nodes) merged.SetNode(locator, node ?? []);
                foreach ((ValueHash256 hash, ulong? count) in content.CodeReferences) merged.SetCodeReference(hash, count);
            }

            PbtSnapshot newest = chainOldestFirst[^1];
            return new PbtSnapshot(chainOldestFirst[0].From, newest.To, newest.TreeRoot, merged, resourcePool, usage);
        }
        catch
        {
            resourcePool.ReturnSnapshotContent(usage, merged);
            throw;
        }
    }
}

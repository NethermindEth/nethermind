// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Memory;
using Nethermind.Pbt;
using Nethermind.Pbt.NodeBuffer;
using Nethermind.State.Flat;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Pbt.PersistedSnapshots;
using Nethermind.State.Pbt.Snapshot;

namespace Nethermind.State.Pbt.Test;

/// <summary>Repository and bundle queries the tests need, composed from their public API.</summary>
internal static class PbtSnapshotTestExtensions
{
    private static readonly SnapshotTier[] MemoryTiers = [SnapshotTier.InMemoryBase, SnapshotTier.InMemoryCompacted];

    /// <summary>Leases the next memory snapshot extending <paramref name="persistedState"/> towards <paramref name="seed"/>.</summary>
    internal static PbtSnapshot? FindSnapshotToPersist(this PbtSnapshotRepository repository, in StateId seed, in StateId persistedState, ulong compactSize)
    {
        (PbtPersistedSnapshot? persisted, PbtSnapshot? snapshot) = repository.FindCandidateToPersist(seed, persistedState, compactSize, static _ => true);
        persisted?.Dispose();
        return snapshot;
    }

    /// <summary>Whether the repository indexes this exact persisted instance.</summary>
    internal static bool ContainsPersistedSource(this PbtSnapshotRepository repository, PbtPersistedSnapshot snapshot)
    {
        if (!repository.TryLeasePersistedCatalogKey(snapshot.To, snapshot.Depth, out PbtPersistedSnapshot? current)) return false;
        using (current) return ReferenceEquals(current, snapshot);
    }

    internal static bool TryLeasePersisted(this PbtSnapshotRepository repository, in StateId to, long depth, SnapshotTier tier, out PbtPersistedSnapshot? snapshot)
    {
        if (repository.TryLeasePersistedCatalogKey(to, depth, out snapshot) && snapshot!.Tier == tier) return true;
        snapshot?.Dispose();
        snapshot = null;
        return false;
    }

    /// <summary>Whether a persisted snapshot of <paramref name="tier"/> spans <paramref name="depth"/> blocks up to <paramref name="to"/>.</summary>
    internal static bool HasPersisted(this PbtSnapshotRepository repository, in StateId to, long depth, SnapshotTier tier)
    {
        if (!repository.TryLeasePersisted(to, depth, tier, out PbtPersistedSnapshot? snapshot)) return false;
        snapshot!.Dispose();
        return true;
    }

    /// <summary>Removes and releases both memory layers at <paramref name="state"/>.</summary>
    internal static void RemoveMemoryState(this PbtSnapshotRepository repository, in StateId state)
    {
        foreach (SnapshotTier tier in MemoryTiers)
        {
            if (!repository.TryLeaseMemoryState(state, tier, out PbtSnapshot? snapshot)) continue;
            using (snapshot) repository.RemoveMemorySource(snapshot!);
        }
    }

    /// <summary>The group record buffer of test layers and bundles that bring none of their own.</summary>
    internal static PbtGroupRecordBuffer DefaultGroupRecordBuffer { get; } = new(PooledRefCountingMemoryProvider.Instance, compressedMaxMemory: 0, liveGenerations: 0);

    /// <summary>Publishes a tree holding <paramref name="groups"/> as the layer's own groups.</summary>
    /// <remarks>
    /// Every ancestor group level a listed group needs, the root included, gets a one-byte placeholder unless listed, so each group
    /// links from its direct parent and its unlisted siblings stay unresolved. A null payload marks its group absent: an absent root
    /// publishes an empty tree, and an absent descendant is unlinked from its parent. Unresolved groups fall back to the persisted view.
    /// </remarks>
    internal static void SetTree(this PbtSnapshotContent content, params (PbtStorageNodePath Path, byte[]? Payload)[] groups)
    {
        content.BeginNodeGroupWrites(content.GroupRecordBuffer ?? DefaultGroupRecordBuffer);
        (PbtGroupReference root, int count, long bytes) = BuildTree(content.GroupRecordBuffer!, groups, fillAncestors: true);
        content.RootGroupReference = root;
        content.AddNodeGroups(count, bytes);
    }

    /// <summary>Publishes a complete physical tree as the layer's own groups, each linked from its nearest physical ancestor across compressed routes.</summary>
    internal static void SetPhysicalTree(this PbtSnapshotContent content, IEnumerable<PbtPhysicalPayload> groups)
    {
        content.BeginNodeGroupWrites(content.GroupRecordBuffer ?? DefaultGroupRecordBuffer);
        (PbtGroupReference root, int count, long bytes) = BuildTree(content.GroupRecordBuffer!,
            [.. groups.Select(static group => (group.Key, (byte[]?)group.Payload.ToArray()))], fillAncestors: false);
        content.RootGroupReference = root;
        content.AddNodeGroups(count, bytes);
    }

    /// <inheritdoc cref="SetTree(PbtSnapshotContent, ValueTuple{PbtStorageNodePath, byte[]}[])"/>
    internal static void SetTree(this PbtSnapshotBundle bundle, params (PbtStorageNodePath Path, byte[]? Payload)[] groups)
    {
        (PbtGroupReference root, int count, long bytes) = BuildTree(bundle.GroupRecordBuffer, groups, fillAncestors: true);
        bundle.PublishNodeGroups(root, count, bytes);
    }

    private static (PbtGroupReference Root, int Count, long Bytes) BuildTree(IPbtGroupRecordBuffer buffer, (PbtStorageNodePath Path, byte[]? Payload)[] groups, bool fillAncestors)
    {
        if (groups.Any(static group => group.Path.BitDepth == 0 && group.Payload is null)) return (PbtGroupReference.None, 0, 0);
        Dictionary<PbtStorageNodePath, byte[]?> tree = groups.ToDictionary(static group => group.Path, static group => group.Payload);
        tree.TryAdd(default, [0]);
        if (fillAncestors)
            foreach ((PbtStorageNodePath path, _) in groups)
                for (int depth = 0; depth < path.BitDepth; depth += PbtThreeLevelGroupGeometry.LevelsPerGroup) tree.TryAdd(path.Prefix(depth), [0]);
        Dictionary<PbtStorageNodePath, PbtGroupReference> published = [];
        int count = 0;
        long bytes = 0;
        foreach ((PbtStorageNodePath path, byte[]? payload) in tree.OrderByDescending(static group => group.Key.BitDepth))
        {
            if (payload is null)
            {
                published[path] = PbtGroupReference.None;
                continue;
            }
            PbtGroupLinks links = PbtGroupLinks.Unknown;
            byte[] childBytes = new byte[PbtVariableTreeKey.MaxLength];
            foreach ((PbtStorageNodePath child, PbtGroupReference reference) in published)
            {
                if (!NearestAncestor(tree, child).Equals(path)) continue;
                Array.Clear(childBytes);
                PbtNodePathOperations.CopyTo(child, childBytes);
                links[TrieUpdater.BoundarySlot(childBytes, path.BitDepth)] = reference;
            }
            published[path] = buffer.Append(path, payload, links);
            count++;
            bytes += ((path.BitDepth + 7) >> 3) + payload.Length;
        }
        return (published[default], count, bytes);
    }

    private static PbtStorageNodePath NearestAncestor(Dictionary<PbtStorageNodePath, byte[]?> tree, PbtStorageNodePath path)
    {
        for (int depth = path.BitDepth - PbtThreeLevelGroupGeometry.LevelsPerGroup; depth > 0; depth -= PbtThreeLevelGroupGeometry.LevelsPerGroup)
            if (tree.TryGetValue(path.Prefix(depth), out byte[]? payload) && payload is not null) return path.Prefix(depth);
        return default;
    }

    /// <summary>Returns a caller-owned group lease, or null when no layer has an entry.</summary>
    internal static RefCountingMemory? GetNodeGroup(this PbtReadOnlySnapshotBundle bundle, PbtStorageNodePath groupKey) =>
        bundle.TryGetSnapshotNodeGroup(groupKey, out RefCountingMemory? payload) ? payload : bundle.GetPersistedNodeGroup(groupKey);
}

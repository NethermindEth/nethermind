// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Nethermind.Core.Crypto;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Pbt;
using Nethermind.Pbt.NodeBuffer;
using Nethermind.State.Flat;
using Nethermind.State.Pbt.Common;
using Nethermind.State.Pbt.NodeBuffer;
using Nethermind.State.Pbt.Persistence;
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
        if (!repository.TryLeasePersistedCatalogKey(snapshot.To, snapshot.Tier, out PbtPersistedSnapshot? current)) return false;
        using (current) return ReferenceEquals(current, snapshot);
    }

    internal static bool TryLeasePersisted(this PbtSnapshotRepository repository, in StateId to, long depth, SnapshotTier tier, out PbtPersistedSnapshot? snapshot)
    {
        if (repository.TryLeasePersistedCatalogKey(to, tier, out snapshot) && snapshot!.Depth == depth) return true;
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

    /// <summary>The current write frontier of <paramref name="buffer"/>, at or below every later write.</summary>
    internal static ulong WriteFrontier(this IPbtGroupRecordBuffer buffer)
    {
        using IPbtGroupRecordBufferPin pin = buffer.PinHead();
        return pin.WriteBoundary;
    }

    /// <summary>The pins <paramref name="buffer"/> holds.</summary>
    internal static int PinCount(this PbtGroupRecordBuffer buffer) =>
        (int)typeof(PbtGroupRecordBuffer).GetField("_pinCount", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(buffer)!;

    /// <summary>The keys added to the bloom of the generation of <paramref name="pointer"/>.</summary>
    internal static long BloomCount(this PbtGroupRecordBuffer buffer, ulong pointer) =>
        ((Nethermind.Core.Collections.BloomFilter)typeof(PbtGroupRecordBuffer).GetMethod("FindBloom", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(buffer, [pointer])!).Count;

    /// <summary>The group record buffer of test layers and bundles that bring none of their own.</summary>
    internal static PbtGroupRecordBuffer DefaultGroupRecordBuffer { get; } = new(0, PbtGroupRecordBuffer.DefaultGenerationBytes);

    /// <summary>Publishes a tree holding <paramref name="groups"/> as the layer's own groups.</summary>
    /// <remarks>
    /// Every ancestor group level a listed group needs, the root included, gets a one-byte placeholder unless listed, so each group
    /// links from its direct parent and its unlisted siblings stay unresolved. A null payload marks its group absent: an absent root
    /// publishes an empty tree, and an absent descendant is unlinked from its parent. Unresolved groups fall back to the persisted view.
    /// </remarks>
    /// <summary>The groups <see cref="PbtSnapshotContent.EnumerateNodeGroups"/> enumerates, in its order.</summary>
    internal static PbtGroupRecord[] NodeGroups(this PbtSnapshotContent content) => [.. content.EnumerateNodeGroups()];

    internal static void SetTree(this PbtSnapshotContent content, params (PbtStorageNodePath Path, byte[]? Payload)[] groups)
    {
        content.BeginNodeGroupWrites(content.GroupRecordBuffer ?? DefaultGroupRecordBuffer);
        content.RootGroupPointer = BuildTree(content.GroupRecordBuffer!, groups, fillAncestors: true);
    }

    /// <summary>Publishes a complete physical tree as the layer's own groups, each linked from its nearest physical ancestor across compressed routes.</summary>
    internal static void SetPhysicalTree(this PbtSnapshotContent content, IEnumerable<PbtPhysicalPayload> groups)
    {
        content.BeginNodeGroupWrites(content.GroupRecordBuffer ?? DefaultGroupRecordBuffer);
        content.RootGroupPointer = BuildTree(content.GroupRecordBuffer!,
            [.. groups.Select(static group => (group.Key, (byte[]?)group.Payload.ToArray()))], fillAncestors: false);
    }

    /// <inheritdoc cref="SetPhysicalTree(PbtSnapshotContent, IEnumerable{PbtPhysicalPayload})"/>
    internal static void SetPhysicalTree(this PbtSnapshotBundle bundle, IEnumerable<PbtPhysicalPayload> groups) =>
        bundle.PublishTreeRoot((default, BuildTree(bundle.GroupRecordBuffer,
            [.. groups.Select(static group => (group.Key, (byte[]?)group.Payload.ToArray()))], fillAncestors: false)));

    /// <inheritdoc cref="SetTree(PbtSnapshotContent, ValueTuple{PbtStorageNodePath, byte[]}[])"/>
    internal static void SetTree(this PbtSnapshotBundle bundle, params (PbtStorageNodePath Path, byte[]? Payload)[] groups) =>
        bundle.PublishTreeRoot((default, BuildTree(bundle.GroupRecordBuffer, groups, fillAncestors: true)));

    private static ulong BuildTree(IPbtGroupRecordBuffer buffer, (PbtStorageNodePath Path, byte[]? Payload)[] groups, bool fillAncestors)
    {
        if (groups.Any(static group => group.Path.BitDepth == 0 && group.Payload is null)) return 0;
        Dictionary<PbtStorageNodePath, byte[]?> tree = groups.ToDictionary(static group => group.Path, static group => group.Payload);
        tree.TryAdd(default, [0]);
        if (fillAncestors)
            foreach ((PbtStorageNodePath path, _) in groups)
                for (int depth = 0; depth < path.BitDepth; depth += PbtThreeLevelGroupGeometry.LevelsPerGroup) tree.TryAdd(path.Prefix(depth), [0]);
        Dictionary<PbtStorageNodePath, ulong> published = [];
        foreach ((PbtStorageNodePath path, byte[]? payload) in tree.OrderByDescending(static group => group.Key.BitDepth))
        {
            if (payload is null)
            {
                published[path] = 0;
                continue;
            }
            PbtGroupLinks links = default;
            byte[] childBytes = new byte[PbtVariableTreeKey.MaxLength];
            foreach ((PbtStorageNodePath child, ulong pointer) in published)
            {
                if (!NearestAncestor(tree, child).Equals(path)) continue;
                Array.Clear(childBytes);
                PbtNodePathOperations.CopyTo(child, childBytes);
                links[TrieUpdater.BoundarySlot(childBytes, path.BitDepth)] = pointer;
            }
            published[path] = buffer.Append(path, payload, links);
        }
        return published[default];
    }

    private static PbtStorageNodePath NearestAncestor(Dictionary<PbtStorageNodePath, byte[]?> tree, PbtStorageNodePath path)
    {
        for (int depth = path.BitDepth - PbtThreeLevelGroupGeometry.LevelsPerGroup; depth > 0; depth -= PbtThreeLevelGroupGeometry.LevelsPerGroup)
            if (tree.TryGetValue(path.Prefix(depth), out byte[]? payload) && payload is not null) return path.Prefix(depth);
        return default;
    }

    /// <summary>Returns a caller-owned copy of the persisted group, or null when no persisted layer has an entry.</summary>
    internal static byte[]? GetNodeGroup(this PbtReadOnlySnapshotBundle bundle, PbtStorageNodePath groupKey)
    {
        List<byte[]> groups = [];
        return bundle.TryLoadPersistedNodeGroup(groupKey, CollectGroup, groups) ? groups.Single() : null;
    }

    /// <summary>Serves <paramref name="reader"/>'s group loads from <paramref name="loadGroup"/>, which a substitute cannot configure as they pass by span.</summary>
    internal static GroupLoadingReader LoadingGroupsFrom(this IPbtPersistence.IReader reader, Func<PbtStorageNodePath, byte[]?> loadGroup) => new(reader, loadGroup);

    /// <summary>A copy of the group <paramref name="reader"/> holds at <paramref name="groupKey"/>, or null when it is absent.</summary>
    internal static byte[]? LoadNodeGroup<TPath>(this IPbtPersistence.IReader reader, TPath groupKey) where TPath : struct, IPbtNodePath<TPath>
    {
        List<byte[]> groups = [];
        return reader.TryLoadNodeGroup(groupKey, CollectGroup, groups) ? groups.Single() : null;
    }

    /// <summary>A caller-owned copy of the group at <paramref name="groupKey"/> in the branch's tree, from its buffer or else as a fold loads it.</summary>
    internal static byte[]? GetNodeGroup(this PbtSnapshotBundle bundle, PbtStorageNodePath groupKey) =>
        bundle.GroupRecordBuffer.TryFindGroup(bundle.RootGroupPointer, groupKey, out PbtGroupRecord record) ? record.Payload.ToArray()
        : LoadPayload((action, groups) => bundle.TryLoadNodeGroup(groupKey, action, groups));

    /// <summary>The payload of the group <paramref name="load"/> hands over, as a record or as a payload, or null when it misses.</summary>
    internal static byte[]? LoadPayload(Func<ReadOnlySpanAction<byte, List<byte[]>>, List<byte[]>, bool> load)
    {
        List<byte[]> groups = [];
        if (!load(CollectGroup, groups)) return null;

        byte[] group = groups.Single();
        return PbtGroupRecordCodec.IsRecord(group) ? new PbtGroupRecord(group, 0).Payload.ToArray() : group;
    }

    /// <summary>Writes the group <paramref name="payload"/> to <paramref name="warmed"/> and indexes it at <paramref name="path"/>, as a warm-up does.</summary>
    /// <returns>The pointer of the written record.</returns>
    internal static ulong Warm(PbtWarmedGroupBuffer warmed, in PbtStorageNodePath path, ReadOnlySpan<byte> payload)
    {
        ulong pointer;
        using (IPbtGroupRecordWriteBatch batch = warmed.StartWriteBatch(PbtGroupRecordCodec.AlignedLength(PbtGroupRecordCodec.Length(payload.Length))))
        {
            payload.CopyTo(PbtGroupRecordCodec.WriteGroup(batch.Bytes, path, default, payload.Length));
            pointer = batch.BasePointer;
        }

        warmed.Index(path, pointer);
        return pointer;
    }

    private static readonly ReadOnlySpanAction<byte, List<byte[]>> CollectGroup = static (group, groups) => groups.Add(group.ToArray());

    /// <summary>Forwards to a reader but loads its groups from a delegate, recording every group asked for.</summary>
    public sealed class GroupLoadingReader(IPbtPersistence.IReader inner, Func<PbtStorageNodePath, byte[]?> loadGroup) : IPbtPersistence.IReader
    {
        private readonly ConcurrentQueue<PbtStorageNodePath> _reads = new();

        /// <summary>The groups asked for since the last <see cref="ClearReads"/>, in order.</summary>
        public PbtStorageNodePath[] Reads => [.. _reads];
        public void ClearReads() => _reads.Clear();

        public StateId CurrentState => inner.CurrentState;
        public ValueHash256 CurrentRoot => inner.CurrentRoot;
        public PbtAccount? GetAccount(in ValueHash256 addressHash) => inner.GetAccount(addressHash);
        public PackedSlotRun GetSlotRun<TKey>(in TKey runKey) where TKey : struct, IPbtKey<TKey> => inner.GetSlotRun(runKey);
        public CodeInfo? GetCode(in ValueHash256 codeHash) => inner.GetCode(codeHash);
        public bool TryLoadNodeGroup<TPath, TArg>(in TPath groupKey, ReadOnlySpanAction<byte, TArg> action, TArg arg)
            where TPath : struct, IPbtNodePath<TPath> where TArg : allows ref struct
        {
            PbtStorageNodePath path = groupKey.ToPath<PbtStorageNodePath>();
            _reads.Enqueue(path);
            if (loadGroup(path) is not { } payload) return false;
            action(payload, arg);
            return true;
        }
        public void Dispose() => inner.Dispose();
    }
}

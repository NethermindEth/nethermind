// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie;

public partial class PatriciaTree
{
    internal const int WarmUpBatchSize = 64;
    internal const int MinWarmUpBatchSize = 8;

    public readonly struct WarmUpEntry(in ValueHash256 key, bool forDelete = false) : IComparable<WarmUpEntry>
    {
        public readonly ValueHash256 Key = key;
        public readonly bool ForDelete = forDelete;
        public int CompareTo(WarmUpEntry other) => Key.CompareTo(other.Key);

        internal byte GetNibble(int index) => (byte)((Key.Bytes[index / 2] >> ((index & 1) == 0 ? 4 : 0)) & 15);
    }

    public void WarmUpPaths(ReadOnlySpan<WarmUpEntry> entries) => WarmUpPaths(entries, false, static _ => false);

    public void WarmUpPaths<TState>(ReadOnlySpan<WarmUpEntry> entries, TState state, Func<TState, bool> shouldStop)
    {
        ArgumentNullException.ThrowIfNull(shouldStop);
        if (RootRef is null) return;
        for (int start = 0; start < entries.Length; start += WarmUpBatchSize)
        {
            if (shouldStop(state)) return;
            ReadOnlySpan<WarmUpEntry> chunk = entries.Slice(start, Math.Min(WarmUpBatchSize, entries.Length - start));
            if (chunk.Length < MinWarmUpBatchSize || TrieStore is not IBatchTrieNodeResolver resolver)
            {
                foreach (WarmUpEntry entry in chunk)
                {
                    if (shouldStop(state)) return;
                    WarmUpPath(entry.Key.Bytes, entry.ForDelete);
                }
            }
            else
            {
                int completed = 0;
                while (completed < chunk.Length && TryWarmUpCachedPath(in chunk[completed]))
                {
                    completed++;
                    if (shouldStop(state)) return;
                }
                ReadOnlySpan<WarmUpEntry> pending = chunk[completed..];
                if (pending.Length >= MinWarmUpBatchSize) WarmUpChunk(pending, resolver, state, shouldStop);
                else
                {
                    foreach (WarmUpEntry entry in pending)
                    {
                        if (shouldStop(state)) return;
                        WarmUpPath(entry.Key.Bytes, entry.ForDelete);
                    }
                }
            }
        }
    }

    internal bool TryWarmUpCachedPath(in WarmUpEntry entry)
    {
        TrieNode? node = RootRef;
        if (node is null) return true;
        if (node.NodeType == NodeType.Unknown && !node.HasRlp) return false;
        TrieNode? parentBranch = null;
        TreePath path = TreePath.Empty;
        return TryWarmUpCachedPath(in entry, ref node, ref path, ref parentBranch, out _);
    }

    private bool TryWarmUpCachedPath(in WarmUpEntry entry, ref TrieNode? node, ref TreePath path,
        ref TrieNode? parentBranch, out bool resolveOnly)
    {
        resolveOnly = false;
        if (node is null) return true;
        if (!TryResolveCachedWarmUpNode(ref node, in path)) return false;
        Span<byte> remainingKey = stackalloc byte[64];
        Nibbles.BytesToNibbleBytes(entry.Key.Bytes, remainingKey);
        remainingKey = remainingKey[path.Length..];
        while (node is not null)
        {
            if (node.IsLeaf || node.IsExtension)
            {
                byte[] key = node.Key!;
                if (Nibbles.CommonPrefixLength(remainingKey, key) != key.Length) return true;
                if (node.IsLeaf)
                {
                    if (!entry.ForDelete || parentBranch is null) return true;
                    int siblingNibble = parentBranch.FindOnlyChild(ignoredChild: path[path.Length - 1]);
                    if (siblingNibble < 0) return true;
                    path.TruncateOne();
                    path.AppendMut(siblingNibble);
                    node = parentBranch.GetChildWithChildPath(TrieStore, ref path, siblingNibble, keepChildRef: true);
                    parentBranch = null;
                    resolveOnly = true;
                    return node is null || TryResolveCachedWarmUpNode(ref node, in path);
                }
                path.AppendMut(key);
                remainingKey = remainingKey[key.Length..];
                node = node.GetChildWithChildPath(TrieStore, ref path, 0, keepChildRef: true);
                parentBranch = null;
            }
            else
            {
                if (remainingKey.IsEmpty) return true;
                int nibble = remainingKey[0];
                path.AppendMut(nibble);
                parentBranch = node;
                node = node.GetChildWithChildPath(TrieStore, ref path, nibble, keepChildRef: true);
                remainingKey = remainingKey[1..];
            }
            if (node is not null && !TryResolveCachedWarmUpNode(ref node, in path)) return false;
        }
        return true;
    }

    private bool TryResolveCachedWarmUpNode(ref TrieNode node, in TreePath path)
    {
        if (node.IsSealed && node.Keccak is not null && (path.Length & 1) != 0)
            node = TrieStore.FindCachedOrUnknown(in path, node.Keccak);
        return node.TryResolveNodeFromRlp(null);
    }

    private void WarmUpChunk<TState>(ReadOnlySpan<WarmUpEntry> entries, IBatchTrieNodeResolver resolver,
        TState state, Func<TState, bool> shouldStop)
    {
        Span<WarmUpWork> work = stackalloc WarmUpWork[WarmUpBatchSize];
        for (int i = 0; i < entries.Length; i++) work[i] = new(0, entries[i]);
        ReadOnlySpan<WarmUpTarget> targets = [new(this, RootRef)];
        WarmUpTargets(targets, work[..entries.Length], new SingleTreeWarmUpReader(resolver), state, shouldStop);
    }

    internal readonly record struct WarmUpTarget(PatriciaTree Tree, TrieNode? Root);

    internal readonly struct WarmUpWork(int target, WarmUpEntry entry) : IComparable<WarmUpWork>
    {
        public readonly int Target = target;
        public readonly WarmUpEntry Entry = entry;
        public int CompareTo(WarmUpWork other)
        {
            int targetOrder = Target.CompareTo(other.Target);
            return targetOrder != 0 ? targetOrder : Entry.CompareTo(other.Entry);
        }
    }

    internal interface IWarmUpReader
    {
        void LoadRlps(scoped ReadOnlySpan<(TreePath Path, Hash256 Hash)> requests, scoped ReadOnlySpan<int> targets, scoped Span<byte[]?> values);
        bool IsUnavailable(Exception exception);
    }

    private readonly struct SingleTreeWarmUpReader(IBatchTrieNodeResolver resolver) : IWarmUpReader
    {
        public void LoadRlps(scoped ReadOnlySpan<(TreePath Path, Hash256 Hash)> requests, scoped ReadOnlySpan<int> targets, scoped Span<byte[]?> values) =>
            resolver.LoadRlps(requests, values);
        public bool IsUnavailable(Exception exception) => false;
    }

    internal static void WarmUpTargets<TReader, TState>(ReadOnlySpan<WarmUpTarget> targets,
        Span<WarmUpWork> work, TReader reader, TState state, Func<TState, bool> shouldStop)
        where TReader : struct, IWarmUpReader, allows ref struct
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(work.Length, WarmUpBatchSize);
        Span<WarmUpWork> keys = work;
        keys.Sort();
        int unique = 0;
        for (int i = 0; i < keys.Length; i++)
        {
            WarmUpWork item = keys[i];
            if (unique > 0 && keys[unique - 1].Target == item.Target && keys[unique - 1].Entry.Key == item.Entry.Key)
                keys[unique - 1] = new(item.Target, new(item.Entry.Key, keys[unique - 1].Entry.ForDelete || item.Entry.ForDelete));
            else
                keys[unique++] = item;
        }

        ArrayPoolListRef<WarmUpFrontier> current = new(keys.Length);
        ArrayPoolListRef<WarmUpFrontier> next = default;
        ArrayPoolListRef<(TreePath Path, Hash256 Hash)> requests = default;
        ArrayPoolListRef<byte[]?> values = default;
        try
        {
            next = new(keys.Length);
            for (int first = 0; first < unique;)
            {
                int last = first + 1;
                int target = keys[first].Target;
                while (last < unique && keys[last].Target == target) last++;
                if (targets[target].Root is { } root) current.Add(new(root, TreePath.Empty, first, last - first, null, target));
                first = last;
            }
            Span<int> requestTargets = stackalloc int[WarmUpBatchSize];
            while (current.Count > 0)
            {
                if (shouldStop(state)) return;
                requests.Clear();
                for (int i = 0; i < current.Count; i++)
                {
                    ref WarmUpFrontier frontier = ref current.AsSpan()[i];
                    try
                    {
                        TrieNode? node = frontier.Node;
                        if (node is null) continue;
                        PatriciaTree tree = targets[frontier.Target].Tree;
                        if (frontier.Count == 1)
                        {
                            if (tree.TryWarmUpCachedPath(in keys[frontier.Start].Entry, ref frontier.Node,
                                    ref frontier.Path, ref frontier.ParentBranch, out bool resolveOnly))
                            {
                                frontier.Node = null;
                                continue;
                            }
                            if (resolveOnly) frontier.Count = 0;
                            node = frontier.Node!;
                        }
                        else if (node.IsSealed && node.Keccak is not null && (frontier.Path.Length & 1) != 0)
                            node = tree.TrieStore.FindCachedOrUnknown(in frontier.Path, node.Keccak);
                        frontier.Node = node;
                        frontier.RlpIndex = -1;
                        if (node.NodeType == NodeType.Unknown && !node.HasRlp && node.Keccak is { } hash)
                        {
                            if (values.Count == 0)
                            {
                                requests = new(keys.Length);
                                values = new(keys.Length, keys.Length);
                            }
                            frontier.RlpIndex = requests.Count;
                            requestTargets[requests.Count] = frontier.Target;
                            requests.Add((frontier.Path, hash));
                        }
                    }
                    catch (Exception exception) when (reader.IsUnavailable(exception))
                    {
                        frontier.Node = null;
                    }
                }
                if (requests.Count > 0)
                {
                    if (shouldStop(state)) return;
                    reader.LoadRlps(requests.AsSpan(), requestTargets[..requests.Count], values.AsSpan()[..requests.Count]);
                }
                if (shouldStop(state)) return;
                for (int i = 0; i < current.Count; i++)
                {
                    if (shouldStop(state)) return;
                    ref WarmUpFrontier frontier = ref current.AsSpan()[i];
                    try
                    {
                        TrieNode? node = frontier.Node;
                        if (node is not null && !node.TryResolveNodeFromRlp(frontier.RlpIndex < 0 ? null : values[frontier.RlpIndex]))
                            frontier.Node = null;
                        if (frontier.Node is not null && frontier.Count > 0)
                            targets[frontier.Target].Tree.AdvanceWarmUpFrontier(in frontier, keys[..unique], ref next);
                    }
                    catch (Exception exception) when (reader.IsUnavailable(exception))
                    {
                        frontier.Node = null;
                    }
                }
                current.Clear();
                ArrayPoolListRef<WarmUpFrontier> swap = current;
                current = next;
                next = swap;
            }
        }
        finally
        {
            current.Dispose();
            next.Dispose();
            requests.Dispose();
            values.Dispose();
        }
    }

    private void AdvanceWarmUpFrontier(in WarmUpFrontier frontier, ReadOnlySpan<WarmUpWork> entries,
        scoped ref ArrayPoolListRef<WarmUpFrontier> next)
    {
        TrieNode node = frontier.Node!;
        TreePath path = frontier.Path;
        int end = frontier.Start + frontier.Count;
        if (node.IsLeaf || node.IsExtension)
        {
            byte[] key = node.Key!;
            int first = -1;
            int count = 0;
            bool forDelete = false;
            for (int i = frontier.Start; i < end; i++)
            {
                if (!MatchesWarmUpPrefix(in entries[i].Entry, path.Length, key)) continue;
                if (first < 0) first = i;
                count++;
                forDelete |= entries[i].Entry.ForDelete;
            }
            if (first < 0) return;
            if (node.IsLeaf)
            {
                if (forDelete && frontier.ParentBranch is not null) WarmUpDeleteSibling(frontier.ParentBranch, ref path);
            }
            else if (key.Length > 0 && path.Length + key.Length <= 64)
            {
                path.AppendMut(key);
                TrieNode? child = node.GetChildWithChildPath(TrieStore, ref path, 0, keepChildRef: true);
                if (child is not null) next.Add(new(child, path, first, count, null, frontier.Target));
            }
            return;
        }

        if (path.Length >= 64) return;
        for (int first = frontier.Start; first < end;)
        {
            int nibble = entries[first].Entry.GetNibble(path.Length);
            int last = first + 1;
            while (last < end && entries[last].Entry.GetNibble(path.Length) == nibble) last++;
            TreePath childPath = path.Append(nibble);
            TrieNode? child = node.GetChildWithChildPath(TrieStore, ref childPath, nibble, keepChildRef: true);
            if (child is not null) next.Add(new(child, childPath, first, last - first, node, frontier.Target));
            first = last;
        }
    }

    private static bool MatchesWarmUpPrefix(in WarmUpEntry entry, int offset, ReadOnlySpan<byte> key)
    {
        if (offset + key.Length > 64) return false;
        for (int i = 0; i < key.Length; i++)
            if (entry.GetNibble(offset + i) != key[i]) return false;
        return true;
    }

    private struct WarmUpFrontier(TrieNode? node, TreePath path, int start, int count, TrieNode? parentBranch, int target)
    {
        public readonly int Target = target;
        public TrieNode? Node = node;
        public TreePath Path = path;
        public readonly int Start = start;
        public int Count = count;
        public TrieNode? ParentBranch = parentBranch;
        public int RlpIndex = -1;
    }
}

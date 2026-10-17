// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie;

public partial class PatriciaTree
{
    private const int WarmUpBatchSize = 64;
    private const int MinWarmUpBatchSize = 8;

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
                WarmUpChunk(chunk, resolver, state, shouldStop);
            }
        }
    }

    private void WarmUpChunk<TState>(ReadOnlySpan<WarmUpEntry> entries, IBatchTrieNodeResolver resolver,
        TState state, Func<TState, bool> shouldStop)
    {
        using ArrayPoolListRef<WarmUpEntry> keys = new(entries);
        keys.AsSpan().Sort();
        int unique = 0;
        for (int i = 0; i < keys.Count; i++)
        {
            WarmUpEntry entry = keys[i];
            if (unique > 0 && keys[unique - 1].Key == entry.Key)
                keys[unique - 1] = new(entry.Key, keys[unique - 1].ForDelete || entry.ForDelete);
            else
                keys[unique++] = entry;
        }

        ArrayPoolListRef<WarmUpFrontier> current = new(WarmUpBatchSize);
        ArrayPoolListRef<WarmUpFrontier> next = new(0);
        try
        {
            next = new(WarmUpBatchSize);
            current.Add(new(RootRef, TreePath.Empty, 0, unique, null));
            using ArrayPoolListRef<(TreePath Path, Hash256 Hash)> requests = new(WarmUpBatchSize);
            using ArrayPoolListRef<byte[]?> values = new(WarmUpBatchSize, WarmUpBatchSize);
            while (current.Count > 0)
            {
                if (shouldStop(state)) return;
                requests.Clear();
                for (int i = 0; i < current.Count; i++)
                {
                    ref WarmUpFrontier frontier = ref current.AsSpan()[i];
                    TrieNode? node = frontier.Node;
                    if (node is null) continue;
                    if (node.IsSealed && node.Keccak is not null && (frontier.Path.Length & 1) != 0)
                        node = TrieStore.FindCachedOrUnknown(in frontier.Path, node.Keccak);
                    frontier.Node = node;
                    frontier.RlpIndex = -1;
                    if (node.NodeType == NodeType.Unknown && !node.HasRlp && node.Keccak is { } hash)
                    {
                        frontier.RlpIndex = requests.Count;
                        requests.Add((frontier.Path, hash));
                    }
                }
                if (requests.Count > 0)
                {
                    if (shouldStop(state)) return;
                    resolver.LoadRlps(requests.AsSpan(), values.AsSpan()[..requests.Count]);
                }
                if (shouldStop(state)) return;
                for (int i = 0; i < current.Count; i++)
                {
                    if (shouldStop(state)) return;
                    ref WarmUpFrontier frontier = ref current.AsSpan()[i];
                    TrieNode? node = frontier.Node;
                    if (node is not null && !node.TryResolveNodeFromRlp(frontier.RlpIndex < 0 ? null : values[frontier.RlpIndex]))
                        frontier.Node = null;
                }
                for (int i = 0; i < current.Count; i++)
                {
                    if (shouldStop(state)) return;
                    ref WarmUpFrontier frontier = ref current.AsSpan()[i];
                    if (frontier.Node is not null) AdvanceWarmUpFrontier(in frontier, keys.AsSpan()[..unique], ref next);
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
        }
    }

    private void AdvanceWarmUpFrontier(in WarmUpFrontier frontier, ReadOnlySpan<WarmUpEntry> entries,
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
                if (!MatchesWarmUpPrefix(in entries[i], path.Length, key)) continue;
                if (first < 0) first = i;
                count++;
                forDelete |= entries[i].ForDelete;
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
                if (child is not null) next.Add(new(child, path, first, count, null));
            }
            return;
        }

        if (path.Length >= 64) return;
        for (int first = frontier.Start; first < end;)
        {
            int nibble = entries[first].GetNibble(path.Length);
            int last = first + 1;
            while (last < end && entries[last].GetNibble(path.Length) == nibble) last++;
            TreePath childPath = path.Append(nibble);
            TrieNode? child = node.GetChildWithChildPath(TrieStore, ref childPath, nibble, keepChildRef: true);
            if (child is not null) next.Add(new(child, childPath, first, last - first, node));
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

    private struct WarmUpFrontier(TrieNode? node, TreePath path, int start, int count, TrieNode? parentBranch)
    {
        public TrieNode? Node = node;
        public TreePath Path = path;
        public readonly int Start = start;
        public readonly int Count = count;
        public readonly TrieNode? ParentBranch = parentBranch;
        public int RlpIndex = -1;
    }
}

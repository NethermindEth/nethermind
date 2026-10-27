// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.ScopeProvider;

/// <summary>
/// Resolves every trie node a storage bulk write will read before it runs: the path of each written key, and the child a
/// branch may collapse into when a delete leaves it one. The walk goes one level at a time over all keys at once, each
/// level's reads in parallel, so a few rounds of parallel reads replace the write's chains of dependent ones.
/// </summary>
/// <remarks>
/// Nodes come from the warmer's store, so they land in the per-block node cache the write reads, and each node resolved
/// here is then linked into its parent, so the write walks to it without a lookup. Loads only: no node changes beyond
/// being resolved and linked, as any walk does.
/// A node that cannot be resolved, or throws, ends the walk below it; the write reads it itself.
/// </remarks>
internal sealed class StorageRootPrefetch
{
    // A level with at most one read to make and at most this many nodes is expanded on the calling thread.
    private const int InlineLevelSize = 64;

    private readonly ITrieNodeResolver _store;
    private readonly PatriciaTree.BulkSetEntry[] _keys;
    private readonly Action<int> _process;
    private Level _current;
    private Level _next;
    private int _loads;
    private int _misses;
    private int _failures;

    private readonly record struct Item(TrieNode Node, TreePath Path, int Start, int End, TrieNode? Parent, int ChildIndex);

    private sealed class Level(int capacity)
    {
        public readonly Item[] Items = ArrayPool<Item>.Shared.Rent(capacity);
        public int Count;
        public int Unresolved;

        public void Add(in Item item)
        {
            Items[Interlocked.Increment(ref Count) - 1] = item;
            if (item.Node.NodeType == NodeType.Unknown) Interlocked.Increment(ref Unresolved);
        }

        public void Reset()
        {
            Array.Clear(Items, 0, Math.Min(Count, Items.Length));
            Count = 0;
            Unresolved = 0;
        }
    }

    private StorageRootPrefetch(ITrieNodeResolver store, ReadOnlySpan<PatriciaTree.BulkSetEntry> entries)
    {
        _store = store;
        _keys = ArrayPool<PatriciaTree.BulkSetEntry>.Shared.Rent(entries.Length);
        // A copy: the write's own list stays as it is.
        Span<PatriciaTree.BulkSetEntry> keys = _keys.AsSpan(0, entries.Length);
        entries.CopyTo(keys);
        keys.Sort();
        // Keyed children of one level take disjoint key ranges, so at most one per key, and each branch adds at most
        // one collapse child.
        _current = new Level(2 * entries.Length + 1);
        _next = new Level(2 * entries.Length + 1);
        _process = Process;
    }

    /// <summary>Resolves the nodes the write of <paramref name="entries"/> into the tree at <paramref name="root"/> reads.</summary>
    /// <param name="pool">Runs each level's reads; without one they run on the worker budget of the calling thread.</param>
    /// <returns>
    /// The nodes read from the store, the levels that read any, the nodes the store did not resolve (missing, bytes of
    /// another node, or undecodable), and the nodes whose processing threw.
    /// </returns>
    public static (int Loads, int Rounds, int Misses, int Failures) Run(TrieNode root, ITrieNodeResolver store,
        ReadOnlySpan<PatriciaTree.BulkSetEntry> entries, WarmReadPool? pool)
    {
        StorageRootPrefetch prefetch = new(store, entries);
        try
        {
            int rounds = prefetch.Walk(root, entries.Length, pool);
            return (prefetch._loads, rounds, prefetch._misses, prefetch._failures);
        }
        finally
        {
            prefetch.Return();
        }
    }

    private int Walk(TrieNode root, int keyCount, WarmReadPool? pool)
    {
        _current.Add(new Item(root, TreePath.Empty, 0, keyCount, null, 0));
        int rounds = 0;
        while (_current.Count != 0)
        {
            int count = _current.Count;
            if (_current.Unresolved <= 1 && count <= InlineLevelSize)
            {
                for (int i = 0; i < count; i++) Process(i);
            }
            else if (pool is not null)
            {
                pool.Run(count, Math.Min(pool.MaxConcurrency, count), _process, CancellationToken.None);
            }
            else
            {
                ParallelUnbalancedWork.For(0, count, _process);
            }

            if (_current.Unresolved != 0) rounds++;
            _current.Reset();
            (_current, _next) = (_next, _current);
        }

        return rounds;
    }

    private void Process(int index)
    {
        try
        {
            Item item = _current.Items[index];
            TrieNode node = item.Node;
            TreePath path = item.Path;
            if (node.NodeType == NodeType.Unknown)
            {
                // A node the warmer is still loading is the same object: reading it again costs no wait, as the
                // level's other reads are in flight anyway, and the resolver keeps whichever decode lands first.
                bool load = !node.HasRlp;
                if (!node.TryResolveNode(_store, ref path))
                {
                    Interlocked.Increment(ref _misses);
                    return;
                }

                if (load) Interlocked.Increment(ref _loads);
                // An unresolved warmer node is kept out of its parent's slot, so it is linked now that it is resolved:
                // the per-block cache that also holds it may evict it before the write gets there.
                item.Parent?.ReplaceChildRef(item.ChildIndex, node);
            }

            if (item.Start == item.End) return;

            if (node.IsBranch) ExpandBranch(in item);
            else if (node.IsExtension) ExpandExtension(in item);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _failures);
        }
    }

    private void ExpandBranch(in Item item)
    {
        TrieNode node = item.Node;
        int depth = item.Path.Length;
        TreePath childPath = item.Path.Append(0);
        int touched = 0;
        bool hasDelete = false;

        // Sorted keys sharing this branch's path: each child's keys are one run.
        for (int start = item.Start, end; start < item.End; start = end)
        {
            int nibble = _keys[start].GetPathNibble(depth);
            hasDelete |= IsDelete(in _keys[start]);
            for (end = start + 1; end < item.End && _keys[end].GetPathNibble(depth) == nibble; end++)
            {
                hasDelete |= IsDelete(in _keys[end]);
            }

            touched |= 1 << nibble;
            childPath.SetLast(nibble);
            TrieNode? child = node.GetChildWithChildPath(_store, ref childPath, nibble, keepChildRef: true);
            if (child is not null) _next.Add(new Item(child, childPath, start, end, node, nibble));
        }

        if (!hasDelete) return;

        // Deletes can leave the branch one child, which MaybeCombineNode resolves. A touched child is on a walked path;
        // an untouched one is reached by none, so it is read here when it is the only one: with two or more the
        // branch survives whatever the deletes do.
        int survivor = node.FindOnlyChildOutside(touched);
        if (survivor < 0) return;

        childPath.SetLast(survivor);
        TrieNode? sibling = node.GetChildWithChildPath(_store, ref childPath, survivor, keepChildRef: true);
        if (sibling is not null) _next.Add(new Item(sibling, childPath, 0, 0, node, survivor));
    }

    private void ExpandExtension(in Item item)
    {
        TrieNode node = item.Node;
        byte[] key = node.Key!;
        int depth = item.Path.Length;

        // Sorted, so the keys that continue below the extension are one run.
        int start = item.Start;
        while (start < item.End && !Continues(in _keys[start], depth, key)) start++;
        int end = start;
        while (end < item.End && Continues(in _keys[end], depth, key)) end++;
        if (start == end) return;

        TreePath childPath = item.Path.Append(key);
        TrieNode? child = node.GetChildWithChildPath(_store, ref childPath, 0, keepChildRef: true);
        if (child is not null) _next.Add(new Item(child, childPath, start, end, node, 0));
    }

    private static bool Continues(in PatriciaTree.BulkSetEntry entry, int depth, byte[] extensionKey)
    {
        if (depth + extensionKey.Length > 2 * Hash256.Size) return false;
        for (int i = 0; i < extensionKey.Length; i++)
        {
            if (entry.GetPathNibble(depth + i) != extensionKey[i]) return false;
        }

        return true;
    }

    private static bool IsDelete(in PatriciaTree.BulkSetEntry entry) => entry.Value is not { Length: > 0 };

    private void Return()
    {
        _current.Reset();
        _next.Reset();
        ArrayPool<Item>.Shared.Return(_current.Items);
        ArrayPool<Item>.Shared.Return(_next.Items);
        ArrayPool<PatriciaTree.BulkSetEntry>.Shared.Return(_keys, clearArray: true);
    }
}

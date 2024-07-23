// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Verkle.Tree.TreeNodes;
using Nethermind.Verkle.Tree.VerkleDb;

namespace Nethermind.Verkle.Tree.Cache;

public class BlockDiffCache(int capacity) : StackQueue<(long, SortedVerkleMemoryDb)>(capacity)
{
    public byte[]? GetLeaf(byte[] key)
    {
        using StackEnumerator diffs = GetStackEnumerator();
        while (diffs.MoveNext())
            if (diffs.Current.Item2.LeafTable.TryGetValue(key.ToArray(), out var node))
                return node;
        return null;
    }

    public byte[]? GetLeaf(byte[] key, long blockNumber)
    {
        using StackEnumerator diffs = GetStackEnumerator();
        while (diffs.MoveNext())
        {
            // TODO: find a better way to do this
            if (diffs.Current.Item1 > blockNumber) continue;
            if (diffs.Current.Item2.LeafTable.TryGetValue(key.ToArray(), out var node)) return node;
        }

        return null;
    }

    public InternalNode? GetInternalNode(byte[] key)
    {
        using StackEnumerator diffs = GetStackEnumerator();
        while (diffs.MoveNext())
            if (diffs.Current.Item2.InternalTable.TryGetValue(key, out InternalNode? node))
                return node!.Clone();
        return null;
    }

    public InternalNode? GetInternalNode(byte[] key, long blockNumber)
    {
        using StackEnumerator diffs = GetStackEnumerator();
        while (diffs.MoveNext())
        {
            // TODO: fina a better way to do this
            if (diffs.Current.Item1 > blockNumber) continue;
            if (diffs.Current.Item2.InternalTable.TryGetValue(key, out InternalNode? node)) return node!.Clone();
        }

        return null;
    }

    public void RemoveDiffs(long noOfDiffsToRemove)
    {
        for (var i = 0; i < noOfDiffsToRemove; i++) Pop(out _);
    }
}

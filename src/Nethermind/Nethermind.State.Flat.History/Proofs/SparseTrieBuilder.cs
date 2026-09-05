// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.History.Proofs;

internal static class SparseTrieBuilder
{
    public static TrieNode? Build(IReadOnlyList<TrieLeaf> leaves, in TreePath nodePath)
    {
        if (leaves.Count == 0) return null;

        TreePath path = nodePath;
        return Build(leaves, 0, leaves.Count, ref path);
    }

    private static TrieNode Build(IReadOnlyList<TrieLeaf> leaves, int start, int end, ref TreePath path)
    {
        int depth = path.Length;
        if (end - start == 1)
        {
            TrieLeaf leaf = leaves[start];
            TrieNode node = TrieNodeFactory.CreateLeaf(NibbleRange(leaf.Path, depth, CommitmentDepthPolicy.MaxTrieDepth), new CappedArray<byte>(leaf.Value));
            node.ResolveKey(NullTrieNodeResolver.Instance, ref path, canBeParallel: false);
            return node;
        }

        int sharedNibbles = SharedNibbleCount(leaves[start].Path, leaves[end - 1].Path, depth);
        int branchDepth = depth + sharedNibbles;
        byte[] sharedPath = NibbleRange(leaves[start].Path, depth, branchDepth);

        TrieNode branch = TrieNodeFactory.CreateBranch();
        TreePath branchPath = path.Append(sharedPath);

        int groupStart = start;
        while (groupStart < end)
        {
            int nibble = NibbleAt(leaves[groupStart].Path, branchDepth);
            int groupEnd = groupStart + 1;
            while (groupEnd < end && NibbleAt(leaves[groupEnd].Path, branchDepth) == nibble) groupEnd++;

            branchPath.AppendMut(nibble);
            branch.SetChild(nibble, Build(leaves, groupStart, groupEnd, ref branchPath));
            branchPath.TruncateOne();

            groupStart = groupEnd;
        }

        branch.ResolveKey(NullTrieNodeResolver.Instance, ref branchPath, canBeParallel: false);
        if (sharedNibbles == 0) return branch;

        TrieNode extension = TrieNodeFactory.CreateExtension(sharedPath, branch);
        extension.ResolveKey(NullTrieNodeResolver.Instance, ref path, canBeParallel: false);
        return extension;
    }

    private static int SharedNibbleCount(in ValueHash256 first, in ValueHash256 last, int fromDepth)
    {
        int shared = 0;
        for (int depth = fromDepth; depth < CommitmentDepthPolicy.MaxTrieDepth; depth++)
        {
            if (NibbleAt(first, depth) != NibbleAt(last, depth)) break;
            shared++;
        }

        return shared;
    }

    private static byte[] NibbleRange(in ValueHash256 path, int fromDepth, int toDepth)
    {
        byte[] nibbles = new byte[toDepth - fromDepth];
        for (int i = 0; i < nibbles.Length; i++)
        {
            nibbles[i] = (byte)NibbleAt(path, fromDepth + i);
        }

        return nibbles;
    }

    private static int NibbleAt(in ValueHash256 path, int depth)
    {
        byte value = path.Bytes[depth / 2];
        return (depth & 1) == 0 ? value >> 4 : value & 0x0F;
    }
}

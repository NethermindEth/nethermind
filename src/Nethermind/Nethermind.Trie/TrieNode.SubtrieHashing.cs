// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Cpu;
using Nethermind.Core.Threading;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie;

public sealed partial class TrieNode
{
    /// <summary>Resolves changed lower subtries before encoding the upper trie.</summary>
    /// <remarks>
    /// Uses the two-nibble partition from Reth v2.0.0's ParallelSparseTrie. Only materialized,
    /// unhashed nodes are visited; unchanged siblings remain blinded. Workers own disjoint
    /// subtrees and finish before their parents are encoded.
    /// </remarks>
    internal bool ResolveSubtrieKeys(ITrieNodeResolver resolver, ICappedArrayPool? bufferPool)
    {
        if (RuntimeInformation.IsSingleProcessor || Keccak is not null || !IsBranch)
        {
            return false;
        }

        using ArrayPoolList<(TrieNode Node, TreePath Path)> subtries = new(BranchesCount * BranchesCount);
        Collect(this, TreePath.Empty, subtries);
        if (subtries.Count < 32)
        {
            return false;
        }

        ParallelUnbalancedWork.For(0, subtries.Count, RuntimeInformation.ParallelOptionsLogicalCores,
            (subtries, resolver, bufferPool), static (i, state) =>
            {
                (TrieNode node, TreePath path) = state.subtries[i];
                node.ResolveKey(state.resolver, ref path, state.bufferPool, canBeParallel: false);
                return state;
            });
        return true;

        static void Collect(TrieNode node, TreePath path, ArrayPoolList<(TrieNode Node, TreePath Path)> subtries)
        {
            if (node.Keccak is not null || node.IsLeaf) return;
            if (path.Length >= 2)
            {
                subtries.Add((node, path));
                return;
            }

            if (node.IsBranch)
            {
                for (int i = 0; i < BranchesCount; i++)
                {
                    if (node._nodeData![i] is TrieNode child)
                    {
                        TreePath childPath = path;
                        childPath.AppendMut(i);
                        Collect(child, childPath, subtries);
                    }
                }
            }
            else if (node.IsExtension && node._nodeData![1] is TrieNode child)
            {
                path.AppendMut(node.Key);
                Collect(child, path, subtries);
            }
        }
    }
}

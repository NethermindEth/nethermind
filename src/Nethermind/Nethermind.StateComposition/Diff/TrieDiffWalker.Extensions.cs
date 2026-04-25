// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.StateComposition.Diff;

internal sealed partial class TrieDiffWalker
{
    private void DiffExtensions(TrieNode oldExt, TrieNode newExt, ref TreePath path, ITrieNodeResolver resolver, bool isStorage, int depth)
    {
        byte[]? oldKey = oldExt.Key;
        byte[]? newKey = newExt.Key;

        if (oldKey is not null && newKey is not null && oldKey.AsSpan().SequenceEqual(newKey))
        {
            RecordNode(NodeType.Extension, oldExt.FullRlp.Length, isStorage, added: false);
            RecordNode(NodeType.Extension, newExt.FullRlp.Length, isStorage, added: true);

            if (trackDepth)
            {
                int d = Math.Min(depth, 15);
                RecordDepthShort(oldExt.FullRlp.Length, d, isStorage, added: false);
                RecordDepthShort(newExt.FullRlp.Length, d, isStorage, added: true);
            }

            Hash256? oldChildHash = oldExt.GetChildHash(1);
            Hash256? newChildHash = newExt.GetChildHash(1);

            int prevLen = path.Length;
            path.AppendMut(oldKey);
            // Structural depth: one level per Add, matching StateCompositionContext.Add
            // which seeds the baseline histogram. Incrementing by oldKey.Length would
            // route diffs into nibble-depth buckets and drift the byte totals (bug #...).
            int childDepth = depth + 1;

            if (oldChildHash is not null && newChildHash is not null)
            {
                DiffSubtree(oldChildHash, newChildHash, ref path, resolver, isStorage, childDepth);
            }
            else
            {
                TreePath oldChildPath = path;
                TrieNode? oldChild = oldExt.GetChildWithChildPath(resolver, ref oldChildPath, 0);
                TreePath newChildPath = path;
                TrieNode? newChild = newExt.GetChildWithChildPath(resolver, ref newChildPath, 0);

                if (oldChild is not null && newChild is not null)
                {
                    oldChild.ResolveNode(resolver, in path);
                    newChild.ResolveNode(resolver, in path);
                    DiffNodes(oldChild, newChild, ref path, resolver, isStorage, childDepth);
                }
                else if (oldChild is not null)
                {
                    oldChild.ResolveNode(resolver, in path);
                    CollectSubtree(oldChild, ref path, resolver, isStorage, added: false, childDepth);
                }
                else if (newChild is not null)
                {
                    newChild.ResolveNode(resolver, in path);
                    CollectSubtree(newChild, ref path, resolver, isStorage, added: true, childDepth);
                }
            }

            path.TruncateMut(prevLen);
        }
        else
        {
            // Prefix mismatch means the trie restructured (e.g. extension split on insert).
            // Use DiffMismatchedNodes to match shared leaves by path instead of independently
            // collecting both subtrees, which would emit spurious CodeHashChange / SlotCountChange
            // events for leaves that exist on both sides.
            DiffMismatchedNodes(oldExt, newExt, ref path, resolver, isStorage, depth);
        }
    }
}

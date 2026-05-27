// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.StateDiff.Core.Diff;

public sealed partial class TrieDiffWalker
{
    private void DiffExtensions(TrieNode oldExt, TrieNode newExt, ref TreePath path, ResolverPair resolvers, bool isStorage)
    {
        byte[]? oldKey = oldExt.Key;
        byte[]? newKey = newExt.Key;

        if (oldKey is not null && newKey is not null && oldKey.AsSpan().SequenceEqual(newKey))
        {
            // Prefix-matched extensions still exist on both sides; record both RLP
            // contributions exactly as the legacy walker did. Mismatched prefixes
            // hand off to DiffMismatchedNodes, which routes through CollectSubtree
            // (and thus picks up byte tracking there).
            RecordNodeBytes(oldExt.FullRlp.Length, isStorage, added: false);
            RecordNodeBytes(newExt.FullRlp.Length, isStorage, added: true);

            Hash256? oldChildHash = oldExt.GetChildHash(1);
            Hash256? newChildHash = newExt.GetChildHash(1);

            int prevLen = path.Length;
            path.AppendMut(oldKey);

            if (oldChildHash is not null && newChildHash is not null)
            {
                DiffSubtree(oldChildHash, newChildHash, ref path, resolvers, isStorage);
            }
            else
            {
                TreePath oldChildPath = path;
                TrieNode? oldChild = oldExt.GetChildWithChildPath(resolvers.Old, ref oldChildPath, 0);
                TreePath newChildPath = path;
                TrieNode? newChild = newExt.GetChildWithChildPath(resolvers.New, ref newChildPath, 0);

                if (oldChild is not null && newChild is not null)
                {
                    oldChild.ResolveNode(resolvers.Old, in path);
                    newChild.ResolveNode(resolvers.New, in path);
                    DiffNodes(oldChild, newChild, ref path, resolvers, isStorage);
                }
                else if (oldChild is not null)
                {
                    oldChild.ResolveNode(resolvers.Old, in path);
                    CollectSubtree(oldChild, ref path, resolvers, isStorage, added: false);
                }
                else if (newChild is not null)
                {
                    newChild.ResolveNode(resolvers.New, in path);
                    CollectSubtree(newChild, ref path, resolvers, isStorage, added: true);
                }
            }

            path.TruncateMut(prevLen);
        }
        else
        {
            // Prefix mismatch means the trie restructured (e.g. extension split on insert).
            // DiffMismatchedNodes matches shared leaves by full path instead of independently
            // collecting both subtrees, which would emit spurious slot/code-hash changes for
            // leaves that exist on both sides.
            DiffMismatchedNodes(oldExt, newExt, ref path, resolvers, isStorage);
        }
    }
}

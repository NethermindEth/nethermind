// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Trie
{
    public class RootCheckVisitor : ITreeVisitor
    {
        public bool HasRoot { get; set; } = true;

        public bool IsFullDbScan => false;

        public bool ShouldVisit(Hash256 nextNode)
        {
            return false;
        }

        public void VisitTree(Hash256 rootHash, TrieVisitContext trieVisitContext)
        {
        }

        public void VisitMissingNode(in TreePath path, Hash256 nodeHash, TrieVisitContext trieVisitContext)
        {
            HasRoot = false;
        }

        public void VisitBranch(in TreePath path, TrieNode node, TrieVisitContext trieVisitContext)
        {
        }

        public void VisitExtension(in TreePath path, TrieNode node, TrieVisitContext trieVisitContext)
        {
        }

        public void VisitLeaf(in TreePath path, TrieNode node, TrieVisitContext trieVisitContext, byte[] value = null)
        {
        }

        public void VisitCode(in TreePath path, Hash256 codeHash, TrieVisitContext trieVisitContext)
        {
        }
    }
}

// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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

        public void VisitMissingNode(Hash256 nodeHash, TrieVisitContext trieVisitContext)
        {
            HasRoot = false;
        }

        public void VisitBranch(TrieNode node, TrieVisitContext trieVisitContext)
        {
        }

        public void VisitExtension(TrieNode node, TrieVisitContext trieVisitContext)
        {
        }

        public void VisitLeaf(TrieNode node, TrieVisitContext trieVisitContext, ReadOnlySpan<byte> value)
        {
        }

        public void VisitCode(Hash256 codeHash, TrieVisitContext trieVisitContext)
        {
        }
    }
}

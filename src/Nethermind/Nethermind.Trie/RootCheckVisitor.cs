// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Trie
{
    public class RootCheckVisitor : ITreeVisitor
    {
        public bool HasRoot { get; set; } = true;

        public bool IsFullDbScan => false;

        public bool ShouldVisit(Keccak nextNode)
        {
            return false;
        }

        public void VisitTree(Keccak rootHash, TrieVisitContext trieVisitContext)
        {
        }

        public void VisitMissingNode(Keccak nodeHash, TrieVisitContext trieVisitContext)
        {
            HasRoot = false;
        }

        public void VisitBranch(TrieNode node, TrieVisitContext trieVisitContext)
        {
        }

        public void VisitExtension(TrieNode node, TrieVisitContext trieVisitContext)
        {
        }

        public void VisitLeaf(TrieNode node, TrieVisitContext trieVisitContext, byte[] value = null)
        {
        }

        public void VisitCode(Keccak codeHash, TrieVisitContext trieVisitContext)
        {
        }
    }
}

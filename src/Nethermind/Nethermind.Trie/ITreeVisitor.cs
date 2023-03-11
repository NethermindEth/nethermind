// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Trie
{
    public interface ITreeVisitor
    {
        bool ShouldVisit(Keccak nextNode);

        void VisitTree(Keccak rootHash, TrieVisitContext trieVisitContext);

        void VisitMissingNode(Keccak nodeHash, TrieVisitContext trieVisitContext);

        void VisitBranch(TrieNode node, TrieVisitContext trieVisitContext);

        void VisitExtension(TrieNode node, TrieVisitContext trieVisitContext);

        void VisitLeaf(TrieNode node, TrieVisitContext trieVisitContext, byte[] value = null);

        void VisitCode(Keccak codeHash, TrieVisitContext trieVisitContext);
    }
}

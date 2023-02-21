// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Trie;
using Nethermind.Verkle.Tree.Nodes;

namespace Nethermind.Verkle.Tree;

public interface IVerkleTreeVisitor
{
    bool ShouldVisit(byte[] nextNode);

    void VisitTree(byte[] rootHash, TrieVisitContext trieVisitContext);

    void VisitMissingNode(byte[] nodeKey, TrieVisitContext trieVisitContext);

    void VisitBranchNode(BranchNode node, TrieVisitContext trieVisitContext);

    void VisitStemNode(StemNode node, TrieVisitContext trieVisitContext);

    void VisitLeafNode(byte[] nodeKey, TrieVisitContext trieVisitContext, byte[]? nodeValue);

}

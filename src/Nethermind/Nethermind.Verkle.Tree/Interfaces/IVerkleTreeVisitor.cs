// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Verkle;
using Nethermind.Trie;
using Nethermind.Verkle.Tree.Nodes;

namespace Nethermind.Verkle.Tree.Interfaces;

public interface IVerkleTreeVisitor
{
    bool ShouldVisit(byte[] nextNode);

    void VisitTree(VerkleCommitment rootHash, TrieVisitContext trieVisitContext);

    void VisitMissingNode(byte[] nodeKey, TrieVisitContext trieVisitContext);

    void VisitBranchNode(InternalNode node, TrieVisitContext trieVisitContext);

    void VisitStemNode(InternalNode node, TrieVisitContext trieVisitContext);

    void VisitLeafNode(ReadOnlySpan<byte> nodeKey, TrieVisitContext trieVisitContext, byte[]? nodeValue);

}

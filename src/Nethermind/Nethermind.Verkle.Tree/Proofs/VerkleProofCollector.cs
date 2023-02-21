// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.Verkle.Tree.Proofs;

public class VerkleProofCollector: ITreeVisitor
{

    public bool ShouldVisit(Keccak nextNode)
    {
        throw new NotImplementedException();
    }
    public void VisitTree(Keccak rootHash, TrieVisitContext trieVisitContext)
    {
        throw new NotImplementedException();
    }
    public void VisitMissingNode(Keccak nodeHash, TrieVisitContext trieVisitContext)
    {
        throw new NotImplementedException();
    }
    public void VisitBranch(TrieNode node, TrieVisitContext trieVisitContext)
    {
        throw new NotImplementedException();
    }
    public void VisitExtension(TrieNode node, TrieVisitContext trieVisitContext)
    {
        throw new NotImplementedException();
    }
    public void VisitLeaf(TrieNode node, TrieVisitContext trieVisitContext, byte[] value = null)
    {
        throw new NotImplementedException();
    }
    public void VisitCode(Keccak codeHash, TrieVisitContext trieVisitContext)
    {
        throw new NotImplementedException();
    }
}

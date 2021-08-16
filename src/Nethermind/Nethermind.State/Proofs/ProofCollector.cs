//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.State.Proofs
{
    /// <summary>
    /// EIP-1186 style proof collector
    /// </summary>
    public class ProofCollector : ITreeVisitor
    {
        private int _pathIndex;

        private readonly byte[] _key;

        private Nibble[] Prefix => Nibbles.FromBytes(_key);

        private HashSet<Keccak> _visitingFilter = new();
        
        private List<byte[]> _proofBits = new();

        public ProofCollector(byte[] key)
        {
            _key = key;
        }

        
        public byte[][] BuildResult() => _proofBits.ToArray();

        public bool ShouldVisit(Keccak nextNode) => _visitingFilter.Contains(nextNode);

        public void VisitTree(Keccak rootHash, TrieVisitContext trieVisitContext)
        {
        }

        public void VisitMissingNode(Keccak nodeHash, TrieVisitContext trieVisitContext)
        {
        }

        public void VisitBranch(TrieNode node, TrieVisitContext trieVisitContext)
        {
            AddProofBits(node);
            _visitingFilter.Remove(node.Keccak);
            _visitingFilter.Add(node.GetChildHash((byte) Prefix[_pathIndex]));

            _pathIndex++;
        }

        public void VisitExtension(TrieNode node, TrieVisitContext trieVisitContext)
        {
            AddProofBits(node);
            _visitingFilter.Remove(node.Keccak);

            Keccak childHash = node.GetChildHash(0);
            _visitingFilter.Add(childHash); // always accept so can optimize

            _pathIndex += node.Path.Length;
        }

        protected virtual void AddProofBits(TrieNode node)
        {
            _proofBits.Add(node.FullRlp);
        }

        public void VisitLeaf(TrieNode node, TrieVisitContext trieVisitContext, byte[] value)
        {
            AddProofBits(node);
            _visitingFilter.Remove(node.Keccak);
            _pathIndex = 0;
        }

        public void VisitCode(Keccak codeHash, TrieVisitContext trieVisitContext)
        {
            throw new InvalidOperationException($"{nameof(AccountProofCollector)} does never expect to visit code");
        }
    }
}

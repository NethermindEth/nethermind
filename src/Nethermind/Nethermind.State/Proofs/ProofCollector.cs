// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

        public bool IsFullDbScan => false;

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
            _visitingFilter.Add(node.GetChildHash((byte)Prefix[_pathIndex]));

            _pathIndex++;
        }

        public void VisitExtension(TrieNode node, TrieVisitContext trieVisitContext)
        {
            AddProofBits(node);
            _visitingFilter.Remove(node.Keccak);

            Keccak childHash = node.GetChildHash(0);
            _visitingFilter.Add(childHash); // always accept so can optimize

            _pathIndex += node.Key.Length;
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

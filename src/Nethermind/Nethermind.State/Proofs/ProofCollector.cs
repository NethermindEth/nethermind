// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.State.Proofs
{
    /// <summary>
    /// EIP-1186 style proof collector
    /// </summary>
    public class ProofCollector : ITreeVisitor<EmptyContext>
    {
        private int _pathIndex;

        private Nibble[] Prefix => Nibbles.FromBytes(_key);

        private readonly HashSet<Hash256AsKey> _visitingFilter = new(Hash256AsKeyComparer.Instance);
        private readonly HashSet<Hash256AsKey>.AlternateLookup<ValueHash256> _visitingFilterLookup;

        private readonly List<byte[]> _proofBits = new();
        private readonly byte[] _key;

        /// <summary>
        /// EIP-1186 style proof collector
        /// </summary>
        public ProofCollector(byte[] key)
        {
            _key = key;
            _visitingFilterLookup = _visitingFilter.GetAlternateLookup<ValueHash256>();
        }

        public byte[][] BuildResult() => _proofBits.ToArray();

        public bool IsFullDbScan => false;
        public bool ExpectAccounts => false;

        public bool ShouldVisit(in EmptyContext _, in ValueHash256 nextNode) => _visitingFilterLookup.Contains(nextNode);

        public void VisitTree(in EmptyContext _, in ValueHash256 rootHash)
        {
        }

        public void VisitMissingNode(in EmptyContext _, in ValueHash256 nodeHash)
        {
        }

        public void VisitBranch(in EmptyContext _, TrieNode node)
        {
            AddProofBits(node);
            _visitingFilter.Remove(node.Keccak);
            _visitingFilter.Add(node.GetChildHash((byte)Prefix[_pathIndex]));

            _pathIndex++;
        }

        public void VisitExtension(in EmptyContext _, TrieNode node)
        {
            AddProofBits(node);
            _visitingFilter.Remove(node.Keccak);

            Hash256 childHash = node.GetChildHash(0);
            _visitingFilter.Add(childHash); // always accept so can optimize

            _pathIndex += node.Key.Length;
        }

        protected virtual void AddProofBits(TrieNode node)
        {
            _proofBits.Add(node.FullRlp.ToArray());
        }

        public void VisitLeaf(in EmptyContext _, TrieNode node)
        {
            AddProofBits(node);
            _visitingFilter.Remove(node.Keccak);
            _pathIndex = 0;
        }

        public void VisitAccount(in EmptyContext _, TrieNode node, in AccountStruct account)
        {
        }
    }
}

// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Proofs;
using Nethermind.Trie;

namespace Nethermind.Synchronization.LesSync
{
    class ChtProofCollector : ProofCollector
    {
        long _fromLevel;
        long _level;
        public ChtProofCollector(byte[] key, long fromLevel) : base(key)
        {
            _fromLevel = fromLevel;
            _level = 0;
        }

        protected override void AddProofBits(TrieNode node)
        {
            if (_level < _fromLevel)
            {
                _level++;
            }
            else
            {
                base.AddProofBits(node);
            }
        }
    }
}

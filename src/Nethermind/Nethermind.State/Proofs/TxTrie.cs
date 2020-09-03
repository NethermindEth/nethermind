//  Copyright (c) 2018 Demerzel Solutions Limited
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
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.State.Proofs
{
    public class TxTrie : PatriciaTree
    {
        private readonly bool _allowProofs;
        private static TransactionDecoder _txDecoder = new TransactionDecoder();

        public TxTrie(Transaction[] txs, bool allowProofs = false)
            : base(allowProofs ? (IDb) new MemDb() : NullDb.Instance, EmptyTreeHash, false, false)
        {
            _allowProofs = allowProofs;
            if (txs.Length == 0)
            {
                return;
            }

            // 3% allocations (2GB) on a Goerli 3M blocks fast sync due to calling transaction encoder hee
            // avoiding it would require pooling byte arrays and passing them as Spans to temporary trees
            // a temporary trie would be a trie that exists to create a state root only and then be disposed of
            for (int i = 0; i < txs.Length; i++)
            {
                Rlp transactionRlp = _txDecoder.Encode(txs[i]);
                Set(Rlp.Encode(i).Bytes, transactionRlp.Bytes);
            }

            // additional 3% 2GB is used here for trie nodes creation and root calculation
            UpdateRootHash();
        }

        public byte[][] BuildProof(int index)
        {
            if (!_allowProofs)
            {
                throw new InvalidOperationException("Cannot build proofs without underlying DB (for now?)");
            }
            
            ProofCollector proofCollector = new ProofCollector(Rlp.Encode(index).Bytes);
            Accept(proofCollector, RootHash, false);
            return proofCollector.BuildResult();
        }
    }
}
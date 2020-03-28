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
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.State.Proofs
{
    public class ReceiptTrie : PatriciaTree
    {
        private readonly bool _allowProofs;
        private static readonly ReceiptMessageDecoder Decoder = new ReceiptMessageDecoder();
        
        public ReceiptTrie(long blockNumber, ISpecProvider specProvider, TxReceipt[] txReceipts, bool allowProofs = false)
            : base(allowProofs ? (IDb) new MemDb() : NullDb.Instance, EmptyTreeHash, false, false)
        {
            _allowProofs = allowProofs;
            if (txReceipts.Length == 0)
            {
                return;
            }
            
            for (int i = 0; i < txReceipts.Length; i++)
            {
                byte[] receiptRlp = Decoder.EncodeNew(txReceipts[i], specProvider.GetSpec(blockNumber).IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None);
                Set(Rlp.Encode(i).Bytes, receiptRlp);
            }

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
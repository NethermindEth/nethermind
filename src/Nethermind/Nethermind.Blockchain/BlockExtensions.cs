/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Specs;
using Nethermind.Store;

namespace Nethermind.Blockchain
{
    public static class BlockExtensions
    {
        private static ReceiptDecoder _receiptDecoder = new ReceiptDecoder();
        private static TransactionDecoder _txDecoder = new TransactionDecoder();
        
        public static Keccak CalculateReceiptRoot(this Block block, ISpecProvider specProvider, TxReceipt[] txReceipts)
        {
            if (txReceipts.Length == 0)
            {
                return PatriciaTree.EmptyTreeHash;
            }
            
            PatriciaTree receiptTree = new PatriciaTree();
            for (int i = 0; i < txReceipts.Length; i++)
            {
                byte[] receiptRlp = _receiptDecoder.EncodeNew(txReceipts[i], specProvider.GetSpec(block.Number).IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None);
                receiptTree.Set(Rlp.Encode(i).Bytes, receiptRlp);
            }

            receiptTree.UpdateRootHash();
            return receiptTree.RootHash;
        }
        
        public static Keccak CalculateTxRoot(this Block block)
        {
            if (block.Transactions.Length == 0)
            {
                return PatriciaTree.EmptyTreeHash;
            }
            
            PatriciaTree txTree = new PatriciaTree();
            for (int i = 0; i < block.Transactions.Length; i++)
            {
                Rlp transactionRlp = _txDecoder.Encode(block.Transactions[i]);
                txTree.Set(Rlp.Encode(i).Bytes, transactionRlp.Bytes);
            }

            txTree.UpdateRootHash();
            return txTree.RootHash;
        }
        
        public static Keccak CalculateOmmersHash(this Block block)
        {
            return block.Ommers.Length == 0
                ? Keccak.OfAnEmptySequenceRlp
                : Keccak.Compute(Rlp.Encode(block.Ommers));
        }
    }
}
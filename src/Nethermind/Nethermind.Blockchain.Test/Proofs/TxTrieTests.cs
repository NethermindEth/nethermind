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

using System.IO;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Proofs
{
    public class TxTrieTests
    {
        [Test]
        public void Can_calculate_root()
        {
            Block block = Build.A.Block.WithTransactions(Build.A.Transaction.TestObject).TestObject;
            TxTrie txTrie = new TxTrie(block.Transactions);
            Assert.AreEqual("0x29cc403075ed3d1d6af940d577125cc378ee5a26f7746cbaf87f1cf4a38258b5", txTrie.RootHash.ToString());
        }
        
        [Test]
        public void Can_collect_proof_trie_case_1()
        {
            Block block = Build.A.Block.WithTransactions(Build.A.Transaction.TestObject).TestObject;
            TxTrie txTrie = new TxTrie(block.Transactions, true);
            byte[][] proof = txTrie.BuildProof(0);
            
            txTrie.UpdateRootHash();
            VerifyProof(proof, txTrie.RootHash);
        }
        
        [Test]
        public void Can_collect_proof_with_trie_case_2()
        {
            Block block = Build.A.Block.WithTransactions(Build.A.Transaction.TestObject, Build.A.Transaction.TestObject).TestObject;
            TxTrie txTrie = new TxTrie(block.Transactions, true);
            byte[][] proof = txTrie.BuildProof(0);
            Assert.AreEqual(2, proof.Length);
            
            txTrie.UpdateRootHash();
            VerifyProof(proof, txTrie.RootHash);
        }
        
        [Test]
        public void Can_collect_proof_with_trie_case_3_modified()
        {
            Block block = Build.A.Block.WithTransactions(Enumerable.Repeat(Build.A.Transaction.TestObject, 1000).ToArray()).TestObject;
            TxTrie txTrie = new TxTrie(block.Transactions, true);

            txTrie.UpdateRootHash();
            for (int i = 0; i < 1000; i++)
            {
                byte[][] proof = txTrie.BuildProof(i);    
                VerifyProof(proof, txTrie.RootHash);    
            }
        }

        private static void VerifyProof(byte[][] proof, Keccak txRoot)
        {
            for (int i = proof.Length; i > 0; i--)
            {
                Keccak proofHash = Keccak.Compute(proof[i - 1]);
                if (i > 1)
                {
                    if (!new Rlp(proof[i - 2]).ToString(false).Contains(proofHash.ToString(false)))
                    {
                        throw new InvalidDataException();
                    }
                }
                else
                {
                    if (proofHash != txRoot)
                    {
                        throw new InvalidDataException();
                    }
                }
            }
        }
    }
}
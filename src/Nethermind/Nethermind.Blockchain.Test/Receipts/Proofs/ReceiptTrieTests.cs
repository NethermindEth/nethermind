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

using System.IO;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.State.Proofs;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Proofs
{
    public class ReceiptTrieTests
    {
        [Test]
        public void Can_calculate_root_no_eip_658()
        {
            TxReceipt receipt = Build.A.Receipt.WithAllFieldsFilled.TestObject;
            ReceiptTrie receiptTrie = new ReceiptTrie(MainnetSpecProvider.Instance.GetSpec(1), new[] {receipt});
            Assert.AreEqual("0xe51a2d9f986d68628990c9d65e45c36128ec7bb697bd426b0bb4d18a3f3321be", receiptTrie.RootHash.ToString());
        }

        [Test]
        public void Can_calculate_root()
        {
            TxReceipt receipt = Build.A.Receipt.WithAllFieldsFilled.TestObject;
            ReceiptTrie receiptTrie = new ReceiptTrie(MainnetSpecProvider.Instance.GetSpec(MainnetSpecProvider.MuirGlacierBlockNumber), new[] {receipt});
            Assert.AreEqual("0x2e6d89c5b539e72409f2e587730643986c2ef33db5e817a4223aa1bb996476d5", receiptTrie.RootHash.ToString());
        }

        [Test]
        public void Can_collect_proof_with_branch()
        {
            TxReceipt receipt1 = Build.A.Receipt.WithAllFieldsFilled.TestObject;
            TxReceipt receipt2 = Build.A.Receipt.WithAllFieldsFilled.TestObject;
            ReceiptTrie trie = new ReceiptTrie(MainnetSpecProvider.Instance.GetSpec(1), new[] {receipt1, receipt2}, true);
            byte[][] proof = trie.BuildProof(0);
            Assert.AreEqual(2, proof.Length);
            
            trie.UpdateRootHash();
            VerifyProof(proof, trie.RootHash);
        }
        
        private static void VerifyProof(byte[][] proof, Keccak receiptRoot)
        {
            TrieNode node = new TrieNode(NodeType.Unknown, proof.Last());
            node.ResolveNode(null);
            TxReceipt receipt = new ReceiptMessageDecoder().Decode(node.Value.AsRlpStream());
            Assert.NotNull(receipt.Bloom);
            
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
                else if (proofHash != receiptRoot)
                {
                    throw new InvalidDataException();
                }
            }
        }
    }
}

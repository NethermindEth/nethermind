// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Nethermind.State.Proofs;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Proofs
{
    [TestFixture(true)]
    [TestFixture(false)]
    public class TxTrieTests
    {
        private readonly IReleaseSpec _releaseSpec;

        public TxTrieTests(bool useEip2718)
        {
            _releaseSpec = useEip2718 ? Berlin.Instance : MuirGlacier.Instance;
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Can_calculate_root()
        {
            Block block = Build.A.Block.WithTransactions(Build.A.Transaction.TestObject).TestObject;
            Keccak rootHash = TxTrie.CalculateRoot(block.Transactions);

            if (_releaseSpec == Berlin.Instance)
            {
                Assert.That(rootHash.ToString(), Is.EqualTo("0x29cc403075ed3d1d6af940d577125cc378ee5a26f7746cbaf87f1cf4a38258b5"));
            }
            else
            {
                Assert.That(rootHash.ToString(), Is.EqualTo("0x29cc403075ed3d1d6af940d577125cc378ee5a26f7746cbaf87f1cf4a38258b5"));
            }
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Can_collect_proof_trie_case_1()
        {
            Block block = Build.A.Block.WithTransactions(Build.A.Transaction.TestObject).TestObject;
            TxTrie txTrie = new(block.Transactions, true);
            byte[][] proof = txTrie.BuildProof(0);

            txTrie.UpdateRootHash();
            VerifyProof(proof, txTrie.RootHash);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Can_collect_proof_with_trie_case_2()
        {
            Block block = Build.A.Block.WithTransactions(Build.A.Transaction.TestObject, Build.A.Transaction.TestObject).TestObject;
            TxTrie txTrie = new(block.Transactions, true);
            byte[][] proof = txTrie.BuildProof(0);
            Assert.That(proof.Length, Is.EqualTo(2));

            txTrie.UpdateRootHash();
            VerifyProof(proof, txTrie.RootHash);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Can_collect_proof_with_trie_case_3_modified()
        {
            Block block = Build.A.Block.WithTransactions(Enumerable.Repeat(Build.A.Transaction.TestObject, 1000).ToArray()).TestObject;
            TxTrie txTrie = new(block.Transactions, true);

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

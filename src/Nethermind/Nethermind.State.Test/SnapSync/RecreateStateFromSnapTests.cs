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

#nullable enable 

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Proofs;
using Nethermind.State.SnapSync;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Store.Test
{
    [TestFixture]
    public class RecreateStateFromSnapTests
    {
        private readonly Account _account0 = Build.An.Account.WithBalance(0).TestObject;
        private readonly Account _account1 = Build.An.Account.WithBalance(1).TestObject;
        private readonly Account _account2 = Build.An.Account.WithBalance(2).TestObject;
        private readonly Account _account3 = Build.An.Account.WithBalance(3).TestObject;
        private readonly Account _account4 = Build.An.Account.WithBalance(4).TestObject;
        private readonly Account _account5 = Build.An.Account.WithBalance(5).TestObject;
        private readonly Account _account6 = Build.An.Account.WithBalance(6).TestObject;

        [SetUp]
        public void Setup()
        {
            Trie.Metrics.TreeNodeHashCalculations = 0;
            Trie.Metrics.TreeNodeRlpDecodings = 0;
            Trie.Metrics.TreeNodeRlpEncodings = 0;
        }


        //[Test]
        public void Test01()
        {
            AccountWithAddressHash[] accountsWithHashes = new AccountWithAddressHash[]
            {
                new AccountWithAddressHash(new Keccak("0000000000000000000000000000000000000000000000000000000001101234"), _account0),
                new AccountWithAddressHash(new Keccak("0000000000000000000000000000000000000000000000000000000001112345"), _account1),
                new AccountWithAddressHash(new Keccak("0000000000000000000000000000000000000000000000000000000001113456"), _account2),
                new AccountWithAddressHash(new Keccak("0000000000000000000000000000000000000000000000000000000001114567"), _account3),
                new AccountWithAddressHash(new Keccak("0000000000000000000000000000000000000000000000000000000001123456"), _account4),
                new AccountWithAddressHash(new Keccak("0000000000000000000000000000000000000000000000000000000001123457"), _account5),
            };

            StateTree inputTree = new StateTree(new TrieStore(new MemDb(), LimboLogs.Instance), LimboLogs.Instance);
            inputTree.Set(accountsWithHashes[0].AddressHash, accountsWithHashes[0].Account);
            inputTree.Set(accountsWithHashes[1].AddressHash, accountsWithHashes[1].Account);
            inputTree.Set(accountsWithHashes[2].AddressHash, accountsWithHashes[2].Account);
            inputTree.Set(accountsWithHashes[3].AddressHash, accountsWithHashes[3].Account);
            inputTree.Set(accountsWithHashes[4].AddressHash, accountsWithHashes[4].Account);
            inputTree.Set(accountsWithHashes[5].AddressHash, accountsWithHashes[5].Account);
            inputTree.Commit(0);

            Keccak rootHash = inputTree.RootHash;   // "0x8c81279168edc449089449bc0f2136fc72c9645642845755633cf259cd97988b"

            AccountProofCollector accountProofCollector = new(accountsWithHashes[0].AddressHash.Bytes);
            inputTree.Accept(accountProofCollector, inputTree.RootHash);
            byte[][]? firstProof = accountProofCollector.BuildResult().Proof;

            accountProofCollector = new(accountsWithHashes[5].AddressHash.Bytes);
            inputTree.Accept(accountProofCollector, inputTree.RootHash);
            byte[][]? lastProof = accountProofCollector.BuildResult().Proof;

            MemDb db = new MemDb();
            TrieStore store = new TrieStore(db, LimboLogs.Instance);
            StateTree tree = new StateTree(store, LimboLogs.Instance);

            IList<TrieNode> nodes = new List<TrieNode>();

            for (int i = 0; i < (firstProof!).Length; i++)
            {
                byte[]? nodeBytes = (firstProof!)[i];
                var node = new TrieNode(NodeType.Unknown, nodeBytes);
                node.ResolveKey(store, i == 0);

                nodes.Add(node);
                if (i < (firstProof!).Length - 1)
                {
                    //IBatch batch = store.GetOrStartNewBatch();
                    //batch[node.Keccak!.Bytes] = nodeBytes;
                    //db.Set(node.Keccak!, nodeBytes);
                }
            }

            for (int i = 0; i < (lastProof!).Length; i++)
            {
                byte[]? nodeBytes = (lastProof!)[i];
                var node = new TrieNode(NodeType.Unknown, nodeBytes);
                node.ResolveKey(store, i == 0);

                nodes.Add(node);
                if (i < (lastProof!).Length - 1)
                {
                    //IBatch batch = store.GetOrStartNewBatch();
                    //batch[node.Keccak!.Bytes] = nodeBytes;
                    //db.Set(node.Keccak!, nodeBytes);
                }
            }

            tree.RootRef = nodes[0];

            tree.Set(accountsWithHashes[0].AddressHash, accountsWithHashes[0].Account);
            tree.Set(accountsWithHashes[1].AddressHash, accountsWithHashes[1].Account);
            tree.Set(accountsWithHashes[2].AddressHash, accountsWithHashes[2].Account);
            tree.Set(accountsWithHashes[3].AddressHash, accountsWithHashes[3].Account);
            tree.Set(accountsWithHashes[4].AddressHash, accountsWithHashes[4].Account);
            tree.Set(accountsWithHashes[5].AddressHash, accountsWithHashes[5].Account);

            tree.Commit(0);

            Assert.AreEqual(inputTree.RootHash, tree.RootHash);
        }

        [Test]
        public void RecreateAccountStateFromOneRange_01()
        {
            AccountWithAddressHash[] accountsWithHashes = new AccountWithAddressHash[]
            {
                new AccountWithAddressHash(new Keccak("0000000000000000000000000000000000000000000000000000000001101234"), _account0),
                new AccountWithAddressHash(new Keccak("0000000000000000000000000000000000000000000000000000000001112345"), _account1),
                new AccountWithAddressHash(new Keccak("0000000000000000000000000000000000000000000000000000000001113456"), _account2),
                new AccountWithAddressHash(new Keccak("0000000000000000000000000000000000000000000000000000000001114567"), _account3),
                new AccountWithAddressHash(new Keccak("0000000000000000000000000000000000000000000000000000000001123456"), _account4),
                new AccountWithAddressHash(new Keccak("0000000000000000000000000000000000000000000000000000000001123457"), _account5),
            };

            StateTree inputTree = new StateTree(new TrieStore(new MemDb(), LimboLogs.Instance), LimboLogs.Instance);
            inputTree.Set(accountsWithHashes[0].AddressHash, accountsWithHashes[0].Account);
            inputTree.Set(accountsWithHashes[1].AddressHash, accountsWithHashes[1].Account);
            inputTree.Set(accountsWithHashes[2].AddressHash, accountsWithHashes[2].Account);
            inputTree.Set(accountsWithHashes[3].AddressHash, accountsWithHashes[3].Account);
            inputTree.Set(accountsWithHashes[4].AddressHash, accountsWithHashes[4].Account);
            inputTree.Set(accountsWithHashes[5].AddressHash, accountsWithHashes[5].Account);
            inputTree.Commit(0);

            Keccak rootHash = inputTree.RootHash;   // "0x8c81279168edc449089449bc0f2136fc72c9645642845755633cf259cd97988b"

            AccountProofCollector accountProofCollector = new(Keccak.Zero.Bytes);
            inputTree.Accept(accountProofCollector, inputTree.RootHash);
            byte[][]? firstProof = accountProofCollector.BuildResult().Proof;
            accountProofCollector = new(accountsWithHashes[5].AddressHash.Bytes);
            inputTree.Accept(accountProofCollector, inputTree.RootHash);
            byte[][]? lastProof = accountProofCollector.BuildResult().Proof;

            MemDb db = new MemDb();
            TrieStore store = new TrieStore(db, LimboLogs.Instance);
            StateTree tree = new StateTree(store, LimboLogs.Instance);

            SnapProvider snapProvider = new(tree, store);

            Keccak? result = snapProvider.AddAccountRange(1, rootHash, Keccak.Zero, accountsWithHashes, firstProof!.Concat(lastProof!).ToArray());
            
            Assert.AreEqual(rootHash, result);
        }

        [Test]
        public void RecreateAccountStateFromOneRange_02()
        {
            AccountWithAddressHash[] accountsWithHashes = new AccountWithAddressHash[]
            {
                new AccountWithAddressHash(new Keccak("0000000000000000000000000000000000000000000000000000000001101234"), _account0),
                new AccountWithAddressHash(new Keccak("0000000000000000000000000000000000000000000000000000000001112345"), _account1),
                new AccountWithAddressHash(new Keccak("0000000000000000000000000000000000000000000000000000000001113456"), _account2),
                new AccountWithAddressHash(new Keccak("0000000000000000000000000000000000000000000000000000000001114567"), _account3),
                new AccountWithAddressHash(new Keccak("0000000000000000000000000000000000000000000000000000000001123456"), _account4),
                new AccountWithAddressHash(new Keccak("0000000000000000000000000000000000000000000000000000000001123457"), _account5),
            };

            StateTree inputTree = new StateTree(new TrieStore(new MemDb(), LimboLogs.Instance), LimboLogs.Instance);
            inputTree.Set(accountsWithHashes[0].AddressHash, accountsWithHashes[0].Account);
            inputTree.Set(accountsWithHashes[1].AddressHash, accountsWithHashes[1].Account);
            inputTree.Set(accountsWithHashes[2].AddressHash, accountsWithHashes[2].Account);
            inputTree.Set(accountsWithHashes[3].AddressHash, accountsWithHashes[3].Account);
            inputTree.Set(accountsWithHashes[4].AddressHash, accountsWithHashes[4].Account);
            inputTree.Set(accountsWithHashes[5].AddressHash, accountsWithHashes[5].Account);
            inputTree.Commit(0);

            Keccak rootHash = inputTree.RootHash;   // "0x8c81279168edc449089449bc0f2136fc72c9645642845755633cf259cd97988b"

            AccountProofCollector accountProofCollector = new(accountsWithHashes[0].AddressHash.Bytes);
            inputTree.Accept(accountProofCollector, inputTree.RootHash);
            byte[][]? firstProof = accountProofCollector.BuildResult().Proof;
            accountProofCollector = new(accountsWithHashes[5].AddressHash.Bytes);
            inputTree.Accept(accountProofCollector, inputTree.RootHash);
            byte[][]? lastProof = accountProofCollector.BuildResult().Proof;

            MemDb db = new MemDb();
            TrieStore store = new TrieStore(db, LimboLogs.Instance);
            StateTree tree = new StateTree(store, LimboLogs.Instance);

            SnapProvider snapProvider = new(tree, store);

            Keccak? result = snapProvider.AddAccountRange(1, rootHash, accountsWithHashes[0].AddressHash, accountsWithHashes, firstProof!.Concat(lastProof!).ToArray());

            Assert.AreEqual(rootHash, result);
        }

        [Test]
        public void RecreateAccountStateFromMultipleRange()
        {
            AccountWithAddressHash[] accountsWithHashes = new AccountWithAddressHash[]
            {
                new AccountWithAddressHash(new Keccak("0000000000000000000000000000000000000000000000000000000001101234"), _account0),
                new AccountWithAddressHash(new Keccak("0000000000000000000000000000000000000000000000000000000001112345"), _account1),
                new AccountWithAddressHash(new Keccak("0000000000000000000000000000000000000000000000000000000001113456"), _account2),
                new AccountWithAddressHash(new Keccak("0000000000000000000000000000000000000000000000000000000001114567"), _account3),
                new AccountWithAddressHash(new Keccak("0000000000000000000000000000000000000000000000000000000001123456"), _account4),
                new AccountWithAddressHash(new Keccak("0000000000000000000000000000000000000000000000000000000001123457"), _account5),
            };

            StateTree inputTree = new StateTree(new TrieStore(new MemDb(), LimboLogs.Instance), LimboLogs.Instance);
            inputTree.Set(accountsWithHashes[0].AddressHash, accountsWithHashes[0].Account);
            inputTree.Set(accountsWithHashes[1].AddressHash, accountsWithHashes[1].Account);
            inputTree.Set(accountsWithHashes[2].AddressHash, accountsWithHashes[2].Account);
            inputTree.Set(accountsWithHashes[3].AddressHash, accountsWithHashes[3].Account);
            inputTree.Set(accountsWithHashes[4].AddressHash, accountsWithHashes[4].Account);
            inputTree.Set(accountsWithHashes[5].AddressHash, accountsWithHashes[5].Account);
            inputTree.Commit(0);

            Keccak rootHash = inputTree.RootHash;   // "0x8c81279168edc449089449bc0f2136fc72c9645642845755633cf259cd97988b"

            // output state
            MemDb db = new MemDb();
            TrieStore store = new TrieStore(db, LimboLogs.Instance);
            StateTree tree = new StateTree(store, LimboLogs.Instance);
            SnapProvider snapProvider = new(tree, store);

            AccountProofCollector accountProofCollector = new(Keccak.Zero.Bytes);
            inputTree.Accept(accountProofCollector, inputTree.RootHash);
            byte[][]? firstProof = accountProofCollector.BuildResult().Proof;
            accountProofCollector = new(accountsWithHashes[1].AddressHash.Bytes);
            inputTree.Accept(accountProofCollector, inputTree.RootHash);
            byte[][]? lastProof = accountProofCollector.BuildResult().Proof;

            Keccak? result1 = snapProvider.AddAccountRange(1, rootHash, Keccak.Zero, accountsWithHashes[0..2], firstProof!.Concat(lastProof!).ToArray());

            accountProofCollector = new(accountsWithHashes[2].AddressHash.Bytes);
            inputTree.Accept(accountProofCollector, inputTree.RootHash);
            firstProof = accountProofCollector.BuildResult().Proof;
            accountProofCollector = new(accountsWithHashes[3].AddressHash.Bytes);
            inputTree.Accept(accountProofCollector, inputTree.RootHash);
            lastProof = accountProofCollector.BuildResult().Proof;

            Keccak? result2 = snapProvider.AddAccountRange(1, rootHash, accountsWithHashes[2].AddressHash, accountsWithHashes[2..4], firstProof!.Concat(lastProof!).ToArray());

            accountProofCollector = new(accountsWithHashes[4].AddressHash.Bytes);
            inputTree.Accept(accountProofCollector, inputTree.RootHash);
            firstProof = accountProofCollector.BuildResult().Proof;
            accountProofCollector = new(accountsWithHashes[5].AddressHash.Bytes);
            inputTree.Accept(accountProofCollector, inputTree.RootHash);
            lastProof = accountProofCollector.BuildResult().Proof;

            Keccak? result3 = snapProvider.AddAccountRange(1, rootHash, accountsWithHashes[4].AddressHash, accountsWithHashes[4..6], firstProof!.Concat(lastProof!).ToArray());

            Assert.AreEqual(rootHash, result1);
            Assert.AreEqual(rootHash, result2);
            Assert.AreEqual(rootHash, result3);
        }

        [Test]
        public void MissingAccountFromRange()
        {
            AccountWithAddressHash[] accountsWithHashes = new AccountWithAddressHash[]
            {
                new AccountWithAddressHash(new Keccak("0000000000000000000000000000000000000000000000000000000001101234"), _account0),
                new AccountWithAddressHash(new Keccak("0000000000000000000000000000000000000000000000000000000001112345"), _account1),
                new AccountWithAddressHash(new Keccak("0000000000000000000000000000000000000000000000000000000001113456"), _account2),
                new AccountWithAddressHash(new Keccak("0000000000000000000000000000000000000000000000000000000001114567"), _account3),
                new AccountWithAddressHash(new Keccak("0000000000000000000000000000000000000000000000000000000001123456"), _account4),
                new AccountWithAddressHash(new Keccak("0000000000000000000000000000000000000000000000000000000001123457"), _account5),
            };

            StateTree inputTree = new StateTree(new TrieStore(new MemDb(), LimboLogs.Instance), LimboLogs.Instance);
            inputTree.Set(accountsWithHashes[0].AddressHash, accountsWithHashes[0].Account);
            inputTree.Set(accountsWithHashes[1].AddressHash, accountsWithHashes[1].Account);
            inputTree.Set(accountsWithHashes[2].AddressHash, accountsWithHashes[2].Account);
            inputTree.Set(accountsWithHashes[3].AddressHash, accountsWithHashes[3].Account);
            inputTree.Set(accountsWithHashes[4].AddressHash, accountsWithHashes[4].Account);
            inputTree.Set(accountsWithHashes[5].AddressHash, accountsWithHashes[5].Account);
            inputTree.Commit(0);

            Keccak rootHash = inputTree.RootHash;   // "0x8c81279168edc449089449bc0f2136fc72c9645642845755633cf259cd97988b"

            // output state
            MemDb db = new MemDb();
            TrieStore store = new TrieStore(db, LimboLogs.Instance);
            StateTree tree = new StateTree(store, LimboLogs.Instance);
            SnapProvider snapProvider = new(tree, store);

            AccountProofCollector accountProofCollector = new(Keccak.Zero.Bytes);
            inputTree.Accept(accountProofCollector, inputTree.RootHash);
            byte[][]? firstProof = accountProofCollector.BuildResult().Proof;
            accountProofCollector = new(accountsWithHashes[1].AddressHash.Bytes);
            inputTree.Accept(accountProofCollector, inputTree.RootHash);
            byte[][]? lastProof = accountProofCollector.BuildResult().Proof;

            Keccak? result1 = snapProvider.AddAccountRange(1, rootHash, Keccak.Zero, accountsWithHashes[0..2], firstProof!.Concat(lastProof!).ToArray());

            accountProofCollector = new(accountsWithHashes[2].AddressHash.Bytes);
            inputTree.Accept(accountProofCollector, inputTree.RootHash);
            firstProof = accountProofCollector.BuildResult().Proof;
            accountProofCollector = new(accountsWithHashes[3].AddressHash.Bytes);
            inputTree.Accept(accountProofCollector, inputTree.RootHash);
            lastProof = accountProofCollector.BuildResult().Proof;

            // missing accountsWithHashes[2]
            Keccak? result2 = snapProvider.AddAccountRange(1, rootHash, accountsWithHashes[2].AddressHash, accountsWithHashes[3..4], firstProof!.Concat(lastProof!).ToArray());

            accountProofCollector = new(accountsWithHashes[4].AddressHash.Bytes);
            inputTree.Accept(accountProofCollector, inputTree.RootHash);
            firstProof = accountProofCollector.BuildResult().Proof;
            accountProofCollector = new(accountsWithHashes[5].AddressHash.Bytes);
            inputTree.Accept(accountProofCollector, inputTree.RootHash);
            lastProof = accountProofCollector.BuildResult().Proof;

            Keccak? result3 = snapProvider.AddAccountRange(1, rootHash, accountsWithHashes[4].AddressHash, accountsWithHashes[4..6], firstProof!.Concat(lastProof!).ToArray());

            Assert.AreEqual(rootHash, result1);
            Assert.AreNotEqual(rootHash, result2);
            Assert.AreEqual(rootHash, result3);
        }

        //[Test]
        public void TestSimpleTree_01()
        {
            List<string> keys = new();

            MemDb db = new MemDb();
            TrieStore? store = new TrieStore(db, LimboLogs.Instance);

            StateTree tree = new StateTree(store, LimboLogs.Instance);
            tree.Set(new Keccak("0000000000000000000000000000000000000000000000000000000001101234"), _account0);
            tree.Set(new Keccak("0000000000000000000000000000000000000000000000000000000001112345"), _account1);
            tree.Set(new Keccak("0000000000000000000000000000000000000000000000000000000001113456"), _account2);
            tree.Set(new Keccak("0000000000000000000000000000000000000000000000000000000001114567"), _account3);
            tree.Set(new Keccak("0000000000000000000000000000000000000000000000000000000001123456"), _account4);
            tree.Set(new Keccak("0000000000000000000000000000000000000000000000000000000001123457"), _account5);
            tree.Commit(0);

            AccountProofCollector accountProofCollector = new(new Keccak("0000000000000000000000000000000000000000000000000000000001123457").Bytes);
            tree.Accept(accountProofCollector, tree.RootHash);
            AccountProof proof = accountProofCollector.BuildResult();

            MemDb db2 = new MemDb();
            TrieStore? store2 = new TrieStore(db2, LimboLogs.Instance);

            StateTree tree2 = new StateTree(store2, LimboLogs.Instance);

            List<TrieNode> nodes = new List<TrieNode>();
            foreach (var p in proof.Proof!)
            {
                TrieNode node = new TrieNode(NodeType.Unknown, p);
                node.ResolveNode(store2);
                nodes.Add(node);
                db2.Set(Keccak.Compute(p).Bytes, p);
                // ADD FAKE nodes with child hashes
            }

            tree2.RootRef = nodes[0];
            //nodes[0].SetChild(0, nodes[1]);
            //nodes[1].SetChild(2, nodes[2]);
            //nodes[2].SetChild(0, nodes[3]);
            //nodes[3].SetChild(7, nodes[4]);

            tree2.Set(new Keccak("0000000000000000000000000000000000000000000000000000000001101234"), _account0);
            tree2.Set(new Keccak("0000000000000000000000000000000000000000000000000000000001112345"), _account1);
            tree2.Set(new Keccak("0000000000000000000000000000000000000000000000000000000001113456"), _account2);
            tree2.Set(new Keccak("0000000000000000000000000000000000000000000000000000000001114567"), _account3);
            tree2.Set(new Keccak("0000000000000000000000000000000000000000000000000000000001123456"), _account4);
            tree2.Set(new Keccak("0000000000000000000000000000000000000000000000000000000001123457"), _account5);

            tree2.Commit(0);
        }

        //[Test]
        public void TestSimpleTree_03()
        {
            MemDb db = new MemDb();
            var store = new TrieStore(db, LimboLogs.Instance);
            StateTree tree = new StateTree(store, LimboLogs.Instance);

            var rlpBytes = Bytes.FromHexString("0xf85180a0e1d088ebe70c3349d6a1e907afa046240ac0538dc44f9d548e3c7f923324c73aa0062a8866a77cb9a45652a4d2cc40ab9530310fb64ca6b847035767c5d40cf9298080808080808080808080808080");

            var node = new TrieNode(NodeType.Unknown, rlpBytes);
            node.ResolveNode(NullTrieNodeResolver.Instance);
            Keccak?[] keccaks = new Keccak?[16];

            for (int childIndex = 15; childIndex >= 0; childIndex--)
            {
                Keccak? keccak = node.GetChildHash(childIndex);
                keccaks[childIndex] = keccak;
            }

            rlpBytes = Bytes.FromHexString("0xf873a03db15b18b2c004bb8dae65a6875cddf207de4e97a3d34ef71cd7f6cc7fbb94eab850f84e808a130ee8e7179044400000a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a0c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470");
            node = new TrieNode(NodeType.Unknown, rlpBytes);
            node.ResolveNode(NullTrieNodeResolver.Instance);

            tree.Commit(0);
            Assert.AreEqual(7, db.WritesCount, "writes"); // extension, branch, leaf, extension, branch, 2x same leaf
            Assert.AreEqual(7, Trie.Metrics.TreeNodeHashCalculations, "hashes");
            Assert.AreEqual(7, Trie.Metrics.TreeNodeRlpEncodings, "encodings");
        }
    }
}

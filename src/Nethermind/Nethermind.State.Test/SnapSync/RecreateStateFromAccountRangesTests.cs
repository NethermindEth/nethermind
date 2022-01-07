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
    public class RecreateStateFromAccountRangesTests
    {
        private StateTree _inputTree = new StateTree(new TrieStore(new MemDb(), LimboLogs.Instance), LimboLogs.Instance);

        private static readonly Account _account0 = Build.An.Account.WithBalance(0).TestObject;
        private static readonly Account _account1 = Build.An.Account.WithBalance(1).TestObject;
        private static readonly Account _account2 = Build.An.Account.WithBalance(2).TestObject;
        private static readonly Account _account3 = Build.An.Account.WithBalance(3).TestObject;
        private static readonly Account _account4 = Build.An.Account.WithBalance(4).TestObject;
        private static readonly Account _account5 = Build.An.Account.WithBalance(5).TestObject;

        private AccountWithAddressHash[] _accountsWithHashes = new AccountWithAddressHash[]
            {
                new AccountWithAddressHash(new Keccak("0000000000000000000000000000000000000000000000000000000001101234"), _account0),
                new AccountWithAddressHash(new Keccak("0000000000000000000000000000000000000000000000000000000001112345"), _account1),
                new AccountWithAddressHash(new Keccak("0000000000000000000000000000000000000000000000000000000001113456"), _account2),
                new AccountWithAddressHash(new Keccak("0000000000000000000000000000000000000000000000000000000001114567"), _account3),
                new AccountWithAddressHash(new Keccak("0000000000000000000000000000000000000000000000000000000001123456"), _account4),
                new AccountWithAddressHash(new Keccak("0000000000000000000000000000000000000000000000000000000001123457"), _account5),
            };

        [OneTimeSetUp]
        public void Setup()
        {
            _inputTree.Set(_accountsWithHashes[0].AddressHash, _accountsWithHashes[0].Account);
            _inputTree.Set(_accountsWithHashes[1].AddressHash, _accountsWithHashes[1].Account);
            _inputTree.Set(_accountsWithHashes[2].AddressHash, _accountsWithHashes[2].Account);
            _inputTree.Set(_accountsWithHashes[3].AddressHash, _accountsWithHashes[3].Account);
            _inputTree.Set(_accountsWithHashes[4].AddressHash, _accountsWithHashes[4].Account);
            _inputTree.Set(_accountsWithHashes[5].AddressHash, _accountsWithHashes[5].Account);
            _inputTree.Commit(0);
        }

        //[Test]
        public void Test01()
        {
            Keccak rootHash = _inputTree.RootHash;   // "0x8c81279168edc449089449bc0f2136fc72c9645642845755633cf259cd97988b"

            AccountProofCollector accountProofCollector = new(_accountsWithHashes[0].AddressHash.Bytes);
            _inputTree.Accept(accountProofCollector, _inputTree.RootHash);
            byte[][]? firstProof = accountProofCollector.BuildResult().Proof;

            accountProofCollector = new(_accountsWithHashes[5].AddressHash.Bytes);
            _inputTree.Accept(accountProofCollector, _inputTree.RootHash);
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

            tree.Set(_accountsWithHashes[0].AddressHash, _accountsWithHashes[0].Account);
            tree.Set(_accountsWithHashes[1].AddressHash, _accountsWithHashes[1].Account);
            tree.Set(_accountsWithHashes[2].AddressHash, _accountsWithHashes[2].Account);
            tree.Set(_accountsWithHashes[3].AddressHash, _accountsWithHashes[3].Account);
            tree.Set(_accountsWithHashes[4].AddressHash, _accountsWithHashes[4].Account);
            tree.Set(_accountsWithHashes[5].AddressHash, _accountsWithHashes[5].Account);

            tree.Commit(0);

            Assert.AreEqual(_inputTree.RootHash, tree.RootHash);
        }

        [Test]
        public void RecreateAccountStateFromOneRangeWithNonExistenceProof()
        {
            Keccak rootHash = _inputTree.RootHash;   // "0x8c81279168edc449089449bc0f2136fc72c9645642845755633cf259cd97988b"

            AccountProofCollector accountProofCollector = new(Keccak.Zero.Bytes);
            _inputTree.Accept(accountProofCollector, _inputTree.RootHash);
            byte[][]? firstProof = accountProofCollector.BuildResult().Proof;
            accountProofCollector = new(_accountsWithHashes[5].AddressHash.Bytes);
            _inputTree.Accept(accountProofCollector, _inputTree.RootHash);
            byte[][]? lastProof = accountProofCollector.BuildResult().Proof;

            MemDb db = new MemDb();
            TrieStore store = new TrieStore(db, LimboLogs.Instance);

            SnapProvider snapProvider = new(store);

            Keccak? result = snapProvider.AddAccountRange(1, rootHash, Keccak.Zero, _accountsWithHashes, firstProof!.Concat(lastProof!).ToArray());
            
            Assert.AreEqual(rootHash, result);
        }

        [Test]
        public void RecreateAccountStateFromOneRangeWithExistenceProof()
        {
            Keccak rootHash = _inputTree.RootHash;   // "0x8c81279168edc449089449bc0f2136fc72c9645642845755633cf259cd97988b"

            AccountProofCollector accountProofCollector = new(_accountsWithHashes[0].AddressHash.Bytes);
            _inputTree.Accept(accountProofCollector, _inputTree.RootHash);
            byte[][]? firstProof = accountProofCollector.BuildResult().Proof;
            accountProofCollector = new(_accountsWithHashes[5].AddressHash.Bytes);
            _inputTree.Accept(accountProofCollector, _inputTree.RootHash);
            byte[][]? lastProof = accountProofCollector.BuildResult().Proof;

            MemDb db = new MemDb();
            TrieStore store = new TrieStore(db, LimboLogs.Instance);

            SnapProvider snapProvider = new(store);

            Keccak? result = snapProvider.AddAccountRange(1, rootHash, _accountsWithHashes[0].AddressHash, _accountsWithHashes, firstProof!.Concat(lastProof!).ToArray());

            Assert.AreEqual(rootHash, result);
        }

        [Test]
        public void RecreateAccountStateFromOneRangeWithoutProof()
        {
            Keccak rootHash = _inputTree.RootHash;   // "0x8c81279168edc449089449bc0f2136fc72c9645642845755633cf259cd97988b"

            MemDb db = new MemDb();
            TrieStore store = new TrieStore(db, LimboLogs.Instance);

            SnapProvider snapProvider = new(store);

            Keccak? result = snapProvider.AddAccountRange(1, rootHash, _accountsWithHashes[0].AddressHash, _accountsWithHashes);

            Assert.AreEqual(rootHash, result);
        }

        [Test]
        public void RecreateAccountStateFromMultipleRange()
        {
            Keccak rootHash = _inputTree.RootHash;   // "0x8c81279168edc449089449bc0f2136fc72c9645642845755633cf259cd97988b"

            // output state
            MemDb db = new MemDb();
            TrieStore store = new TrieStore(db, LimboLogs.Instance);
            SnapProvider snapProvider = new(store);

            AccountProofCollector accountProofCollector = new(Keccak.Zero.Bytes);
            _inputTree.Accept(accountProofCollector, _inputTree.RootHash);
            byte[][]? firstProof = accountProofCollector.BuildResult().Proof;
            accountProofCollector = new(_accountsWithHashes[1].AddressHash.Bytes);
            _inputTree.Accept(accountProofCollector, _inputTree.RootHash);
            byte[][]? lastProof = accountProofCollector.BuildResult().Proof;

            Keccak? result1 = snapProvider.AddAccountRange(1, rootHash, Keccak.Zero, _accountsWithHashes[0..2], firstProof!.Concat(lastProof!).ToArray());

            accountProofCollector = new(_accountsWithHashes[2].AddressHash.Bytes);
            _inputTree.Accept(accountProofCollector, _inputTree.RootHash);
            firstProof = accountProofCollector.BuildResult().Proof;
            accountProofCollector = new(_accountsWithHashes[3].AddressHash.Bytes);
            _inputTree.Accept(accountProofCollector, _inputTree.RootHash);
            lastProof = accountProofCollector.BuildResult().Proof;

            Keccak? result2 = snapProvider.AddAccountRange(1, rootHash, _accountsWithHashes[2].AddressHash, _accountsWithHashes[2..4], firstProof!.Concat(lastProof!).ToArray());

            accountProofCollector = new(_accountsWithHashes[4].AddressHash.Bytes);
            _inputTree.Accept(accountProofCollector, _inputTree.RootHash);
            firstProof = accountProofCollector.BuildResult().Proof;
            accountProofCollector = new(_accountsWithHashes[5].AddressHash.Bytes);
            _inputTree.Accept(accountProofCollector, _inputTree.RootHash);
            lastProof = accountProofCollector.BuildResult().Proof;

            Keccak? result3 = snapProvider.AddAccountRange(1, rootHash, _accountsWithHashes[4].AddressHash, _accountsWithHashes[4..6], firstProof!.Concat(lastProof!).ToArray());

            Assert.AreEqual(rootHash, result1);
            Assert.AreEqual(rootHash, result2);
            Assert.AreEqual(rootHash, result3);
        }

        [Test]
        public void MissingAccountFromRange()
        {
            Keccak rootHash = _inputTree.RootHash;   // "0x8c81279168edc449089449bc0f2136fc72c9645642845755633cf259cd97988b"

            // output state
            MemDb db = new MemDb();
            TrieStore store = new TrieStore(db, LimboLogs.Instance);
            SnapProvider snapProvider = new(store);

            AccountProofCollector accountProofCollector = new(Keccak.Zero.Bytes);
            _inputTree.Accept(accountProofCollector, _inputTree.RootHash);
            byte[][]? firstProof = accountProofCollector.BuildResult().Proof;
            accountProofCollector = new(_accountsWithHashes[1].AddressHash.Bytes);
            _inputTree.Accept(accountProofCollector, _inputTree.RootHash);
            byte[][]? lastProof = accountProofCollector.BuildResult().Proof;

            Keccak? result1 = snapProvider.AddAccountRange(1, rootHash, Keccak.Zero, _accountsWithHashes[0..2], firstProof!.Concat(lastProof!).ToArray());

            accountProofCollector = new(_accountsWithHashes[2].AddressHash.Bytes);
            _inputTree.Accept(accountProofCollector, _inputTree.RootHash);
            firstProof = accountProofCollector.BuildResult().Proof;
            accountProofCollector = new(_accountsWithHashes[3].AddressHash.Bytes);
            _inputTree.Accept(accountProofCollector, _inputTree.RootHash);
            lastProof = accountProofCollector.BuildResult().Proof;

            // missing _accountsWithHashes[2]
            Keccak? result2 = snapProvider.AddAccountRange(1, rootHash, _accountsWithHashes[2].AddressHash, _accountsWithHashes[3..4], firstProof!.Concat(lastProof!).ToArray());

            accountProofCollector = new(_accountsWithHashes[4].AddressHash.Bytes);
            _inputTree.Accept(accountProofCollector, _inputTree.RootHash);
            firstProof = accountProofCollector.BuildResult().Proof;
            accountProofCollector = new(_accountsWithHashes[5].AddressHash.Bytes);
            _inputTree.Accept(accountProofCollector, _inputTree.RootHash);
            lastProof = accountProofCollector.BuildResult().Proof;

            Keccak? result3 = snapProvider.AddAccountRange(1, rootHash, _accountsWithHashes[4].AddressHash, _accountsWithHashes[4..6], firstProof!.Concat(lastProof!).ToArray());

            Assert.AreEqual(rootHash, result1);
            Assert.AreNotEqual(rootHash, result2);
            Assert.AreEqual(rootHash, result3);
        }
    }
}

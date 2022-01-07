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
    public class RecreateStateFromStorageRangesTests
    {
        private Account? _account;
        private Keccak _accountAddress = new Keccak("0000000000000000000000000000000000000000000000000000000000001234");
        private TrieStore? _store;
        private StateTree? _inputStateTree;
        private StorageTree? _inputStorageTree;

        private SlotWithKeyHash[] _slots = new SlotWithKeyHash[]
        {
                new SlotWithKeyHash(new Keccak("0000000000000000000000000000000000000000000000000000000001101234"), Bytes.FromHexString("0xab12000000000000000000000000000000000000000000000000000000000000000000000000000000")),
                new SlotWithKeyHash(new Keccak("0000000000000000000000000000000000000000000000000000000001112345"), Bytes.FromHexString("0xab34000000000000000000000000000000000000000000000000000000000000000000000000000000")),
                new SlotWithKeyHash(new Keccak("0000000000000000000000000000000000000000000000000000000001113456"), Bytes.FromHexString("0xab56000000000000000000000000000000000000000000000000000000000000000000000000000000")),
                new SlotWithKeyHash(new Keccak("0000000000000000000000000000000000000000000000000000000001114567"), Bytes.FromHexString("0xab78000000000000000000000000000000000000000000000000000000000000000000000000000000")),
                new SlotWithKeyHash(new Keccak("0000000000000000000000000000000000000000000000000000000001123456"), Bytes.FromHexString("0xab90000000000000000000000000000000000000000000000000000000000000000000000000000000")),
                new SlotWithKeyHash(new Keccak("0000000000000000000000000000000000000000000000000000000001123457"), Bytes.FromHexString("0xab9a000000000000000000000000000000000000000000000000000000000000000000000000000000")),
        };

        [OneTimeSetUp]
        public void Setup()
        {
            _store = new TrieStore(new MemDb(), LimboLogs.Instance);
            _inputStorageTree = new StorageTree(_store, LimboLogs.Instance);

            _inputStorageTree.Set(_slots[0].KeyHash, _slots[0].SlotValue);
            _inputStorageTree.Set(_slots[1].KeyHash, _slots[1].SlotValue);
            _inputStorageTree.Set(_slots[2].KeyHash, _slots[2].SlotValue);
            _inputStorageTree.Set(_slots[3].KeyHash, _slots[3].SlotValue);
            _inputStorageTree.Set(_slots[4].KeyHash, _slots[4].SlotValue);
            _inputStorageTree.Set(_slots[5].KeyHash, _slots[5].SlotValue);

            _inputStorageTree.Commit(0);

            _account = Build.An.Account.WithBalance(1).WithStorageRoot(_inputStorageTree.RootHash).TestObject;
            _inputStateTree = new StateTree(_store, LimboLogs.Instance);
            _inputStateTree.Set(_accountAddress, _account);
            _inputStateTree.Commit(0);
        }

        [Test]
        public void RecreateStorageStateFromOneRangeWithNonExistenceProof()
        {
            Keccak rootHash = _inputStorageTree!.RootHash;   // "..."

            AccountProofCollector accountProofCollector = new(_accountAddress.Bytes, new Keccak[] { Keccak.Zero, _slots[5].KeyHash });
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            var proof = accountProofCollector.BuildResult();

            MemDb db = new MemDb();
            TrieStore store = new TrieStore(db, LimboLogs.Instance);

            SnapProvider snapProvider = new(store);

            Keccak? result = snapProvider.AddStorageRange(1, rootHash, Keccak.Zero, _slots, proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray());

            Assert.AreEqual(rootHash, result);
        }

        [Test]
        public void RecreateAccountStateFromOneRangeWithExistenceProof()
        {
            Keccak rootHash = _inputStorageTree!.RootHash;   // "..."

            AccountProofCollector accountProofCollector = new(_accountAddress.Bytes, new Keccak[] { _slots[0].KeyHash, _slots[5].KeyHash });
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            var proof = accountProofCollector.BuildResult();

            MemDb db = new MemDb();
            TrieStore store = new TrieStore(db, LimboLogs.Instance);

            SnapProvider snapProvider = new(store);

            Keccak? result = snapProvider.AddStorageRange(1, rootHash, Keccak.Zero, _slots, proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray());

            Assert.AreEqual(rootHash, result);
        }

        [Test]
        public void RecreateStorageStateFromOneRangeWithoutProof()
        {
            Keccak rootHash = _inputStorageTree!.RootHash;   // "..."

            MemDb db = new MemDb();
            TrieStore store = new TrieStore(db, LimboLogs.Instance);

            SnapProvider snapProvider = new(store);

            Keccak? result = snapProvider.AddStorageRange(1, rootHash, _slots[0].KeyHash, _slots);

            Assert.AreEqual(rootHash, result);
        }

        [Test]
        public void RecreateAccountStateFromMultipleRange()
        {
            Keccak rootHash = _inputStorageTree!.RootHash;   // "..."

            // output state
            MemDb db = new MemDb();
            TrieStore store = new TrieStore(db, LimboLogs.Instance);
            SnapProvider snapProvider = new(store);

            AccountProofCollector accountProofCollector = new(_accountAddress.Bytes, new Keccak[] { Keccak.Zero, _slots[1].KeyHash });
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            var proof = accountProofCollector.BuildResult();

            Keccak? result1 = snapProvider.AddStorageRange(1, rootHash, Keccak.Zero, _slots[0..2], proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray());

            accountProofCollector = new(_accountAddress.Bytes, new Keccak[] { _slots[2].KeyHash, _slots[3].KeyHash });
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            proof = accountProofCollector.BuildResult();

            Keccak? result2 = snapProvider.AddStorageRange(1, rootHash, _slots[2].KeyHash, _slots[2..4], proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray());

            accountProofCollector = new(_accountAddress.Bytes, new Keccak[] { _slots[4].KeyHash, _slots[5].KeyHash });
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            proof = accountProofCollector.BuildResult();

            Keccak? result3 = snapProvider.AddStorageRange(1, rootHash, _slots[4].KeyHash, _slots[4..6], proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray());

            Assert.AreEqual(rootHash, result1);
            Assert.AreEqual(rootHash, result2);
            Assert.AreEqual(rootHash, result3);
        }

        [Test]
        public void MissingAccountFromRange()
        {
            Keccak rootHash = _inputStorageTree!.RootHash;   // "..."

            // output state
            MemDb db = new MemDb();
            TrieStore store = new TrieStore(db, LimboLogs.Instance);
            SnapProvider snapProvider = new(store);

            AccountProofCollector accountProofCollector = new(_accountAddress.Bytes, new Keccak[] { Keccak.Zero, _slots[1].KeyHash });
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            var proof = accountProofCollector.BuildResult();

            Keccak? result1 = snapProvider.AddStorageRange(1, rootHash, Keccak.Zero, _slots[0..2], proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray());

            accountProofCollector = new(_accountAddress.Bytes, new Keccak[] { _slots[2].KeyHash, _slots[3].KeyHash });
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            proof = accountProofCollector.BuildResult();

            Keccak? result2 = snapProvider.AddStorageRange(1, rootHash, _slots[2].KeyHash, _slots[3..4], proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray());

            accountProofCollector = new(_accountAddress.Bytes, new Keccak[] { _slots[4].KeyHash, _slots[5].KeyHash });
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            proof = accountProofCollector.BuildResult();

            Keccak? result3 = snapProvider.AddStorageRange(1, rootHash, _slots[4].KeyHash, _slots[4..6], proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray());

            Assert.AreEqual(rootHash, result1);
            Assert.AreNotEqual(rootHash, result2);
            Assert.AreEqual(rootHash, result3);
        }
    }
}

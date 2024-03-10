// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable disable

using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Proofs;
using Nethermind.State.Snap;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Store.Test
{
    [TestFixture]
    public class RecreateStateFromStorageRangesTests
    {

        private TrieStore _store;
        private StateTree _inputStateTree;
        private StorageTree _inputStorageTree;
        private PathWithAccount _pathWithAccount = new PathWithAccount(TestItem.Tree.AccountAddress0.ValueHash256, new Account(UInt256.Zero));
        private Hash256 _accountAddr;

        [OneTimeSetUp]
        public void Setup()
        {
            _store = new TrieStore(new MemDb(), LimboLogs.Instance);
            (_inputStateTree, _inputStorageTree, _accountAddr) = TestItem.Tree.GetTrees(_store);
        }

        [OneTimeTearDown]
        public void TearDown() => _store?.Dispose();

        [Test]
        public void RecreateStorageStateFromOneRangeWithNonExistenceProof()
        {
            Hash256 rootHash = _inputStorageTree!.RootHash;   // "..."

            AccountProofCollector accountProofCollector = new(TestItem.Tree.AccountAddress0.Bytes, new ValueHash256[] { Keccak.Zero, TestItem.Tree.SlotsWithPaths[5].Path });
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            var proof = accountProofCollector.BuildResult();

            MemDb db = new();
            DbProvider dbProvider = new();
            dbProvider.RegisterDb(DbNames.State, db);
            using ProgressTracker progressTracker = new(null, dbProvider.GetDb<IDb>(DbNames.State), LimboLogs.Instance);
            SnapProvider snapProvider = new(progressTracker, dbProvider, LimboLogs.Instance);
            var result = snapProvider.AddStorageRange(1, null, rootHash, Keccak.Zero, TestItem.Tree.SlotsWithPaths, proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray());

            Assert.That(result, Is.EqualTo(AddRangeResult.OK));
        }

        [Test]
        public void RecreateAccountStateFromOneRangeWithExistenceProof()
        {
            Hash256 rootHash = _inputStorageTree!.RootHash;   // "..."

            AccountProofCollector accountProofCollector = new(TestItem.Tree.AccountAddress0.Bytes, new ValueHash256[] { TestItem.Tree.SlotsWithPaths[0].Path, TestItem.Tree.SlotsWithPaths[5].Path });
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            var proof = accountProofCollector.BuildResult();

            MemDb db = new();
            DbProvider dbProvider = new();
            dbProvider.RegisterDb(DbNames.State, db);
            using ProgressTracker progressTracker = new(null, dbProvider.GetDb<IDb>(DbNames.State), LimboLogs.Instance);
            SnapProvider snapProvider = new(progressTracker, dbProvider, LimboLogs.Instance);
            var result = snapProvider.AddStorageRange(1, null, rootHash, Keccak.Zero, TestItem.Tree.SlotsWithPaths, proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray());

            Assert.That(result, Is.EqualTo(AddRangeResult.OK));
        }

        [Test]
        public void RecreateStorageStateFromOneRangeWithoutProof()
        {
            Hash256 rootHash = _inputStorageTree!.RootHash;   // "..."

            MemDb db = new MemDb();
            DbProvider dbProvider = new();
            dbProvider.RegisterDb(DbNames.State, db);
            using ProgressTracker progressTracker = new(null, dbProvider.GetDb<IDb>(DbNames.State), LimboLogs.Instance);
            SnapProvider snapProvider = new(progressTracker, dbProvider, LimboLogs.Instance);
            var result = snapProvider.AddStorageRange(1, null, rootHash, TestItem.Tree.SlotsWithPaths[0].Path, TestItem.Tree.SlotsWithPaths);

            Assert.That(result, Is.EqualTo(AddRangeResult.OK));
        }

        [Test]
        public void RecreateAccountStateFromMultipleRange()
        {
            Hash256 rootHash = _inputStorageTree!.RootHash;   // "..."

            // output state
            MemDb db = new MemDb();
            DbProvider dbProvider = new();
            dbProvider.RegisterDb(DbNames.State, db);
            using ProgressTracker progressTracker = new(null, dbProvider.GetDb<IDb>(DbNames.State), LimboLogs.Instance);
            SnapProvider snapProvider = new(progressTracker, dbProvider, LimboLogs.Instance);

            AccountProofCollector accountProofCollector = new(TestItem.Tree.AccountAddress0.Bytes, new ValueHash256[] { Keccak.Zero, TestItem.Tree.SlotsWithPaths[1].Path });
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            var proof = accountProofCollector.BuildResult();

            var result1 = snapProvider.AddStorageRange(1, null, rootHash, Keccak.Zero, TestItem.Tree.SlotsWithPaths[0..2], proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray());

            accountProofCollector = new(TestItem.Tree.AccountAddress0.Bytes, new ValueHash256[] { TestItem.Tree.SlotsWithPaths[2].Path, TestItem.Tree.SlotsWithPaths[3].Path });
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            proof = accountProofCollector.BuildResult();

            var result2 = snapProvider.AddStorageRange(1, null, rootHash, TestItem.Tree.SlotsWithPaths[2].Path, TestItem.Tree.SlotsWithPaths[2..4], proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray());

            accountProofCollector = new(TestItem.Tree.AccountAddress0.Bytes, new ValueHash256[] { TestItem.Tree.SlotsWithPaths[4].Path, TestItem.Tree.SlotsWithPaths[5].Path });
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            proof = accountProofCollector.BuildResult();

            var result3 = snapProvider.AddStorageRange(1, null, rootHash, TestItem.Tree.SlotsWithPaths[4].Path, TestItem.Tree.SlotsWithPaths[4..6], proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray());

            Assert.That(result1, Is.EqualTo(AddRangeResult.OK));
            Assert.That(result2, Is.EqualTo(AddRangeResult.OK));
            Assert.That(result3, Is.EqualTo(AddRangeResult.OK));
        }

        [Test]
        public void MissingAccountFromRange()
        {
            Hash256 rootHash = _inputStorageTree!.RootHash;   // "..."

            // output state
            MemDb db = new MemDb();
            DbProvider dbProvider = new();
            dbProvider.RegisterDb(DbNames.State, db);
            using ProgressTracker progressTracker = new(null, dbProvider.GetDb<IDb>(DbNames.State), LimboLogs.Instance);
            SnapProvider snapProvider = new(progressTracker, dbProvider, LimboLogs.Instance);

            AccountProofCollector accountProofCollector = new(TestItem.Tree.AccountAddress0.Bytes, new ValueHash256[] { Keccak.Zero, TestItem.Tree.SlotsWithPaths[1].Path });
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            var proof = accountProofCollector.BuildResult();

            var result1 = snapProvider.AddStorageRange(1, null, rootHash, Keccak.Zero, TestItem.Tree.SlotsWithPaths[0..2], proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray());

            accountProofCollector = new(TestItem.Tree.AccountAddress0.Bytes, new ValueHash256[] { TestItem.Tree.SlotsWithPaths[2].Path, TestItem.Tree.SlotsWithPaths[3].Path });
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            proof = accountProofCollector.BuildResult();

            var result2 = snapProvider.AddStorageRange(1, null, rootHash, TestItem.Tree.SlotsWithPaths[2].Path, TestItem.Tree.SlotsWithPaths[3..4], proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray());

            accountProofCollector = new(TestItem.Tree.AccountAddress0.Bytes, new ValueHash256[] { TestItem.Tree.SlotsWithPaths[4].Path, TestItem.Tree.SlotsWithPaths[5].Path });
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            proof = accountProofCollector.BuildResult();

            var result3 = snapProvider.AddStorageRange(1, null, rootHash, TestItem.Tree.SlotsWithPaths[4].Path, TestItem.Tree.SlotsWithPaths[4..6], proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray());

            Assert.That(result1, Is.EqualTo(AddRangeResult.OK));
            Assert.That(result2, Is.EqualTo(AddRangeResult.DifferentRootHash));
            Assert.That(result3, Is.EqualTo(AddRangeResult.OK));
        }
    }
}

// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable disable

using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Proofs;
using Nethermind.State.Snap;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.SnapSync
{
    [TestFixture(TrieNodeResolverCapability.Hash)]
    [TestFixture(TrieNodeResolverCapability.Path)]
    public class RecreateStateFromStorageRangesTests
    {

        private readonly TrieNodeResolverCapability _resolverCapability;

        public RecreateStateFromStorageRangesTests(TrieNodeResolverCapability resolverCapability)
        {
            _resolverCapability = resolverCapability;
        }

        private ITrieStore _store;
        private IStateTree _inputStateTree;
        private StorageTree _inputStorageTree;

        [OneTimeSetUp]
        public void Setup()
        {
            _store = _resolverCapability.CreateTrieStore(new MemColumnsDb<StateColumns>(), LimboLogs.Instance);
            if (_resolverCapability == TrieNodeResolverCapability.Hash)
            {
                (_inputStateTree, _inputStorageTree) = TestItem.Tree.GetTrees(_store);
            }
            else
            {
                (_inputStateTree, _inputStorageTree) = TestItem.Tree.GetTreesByPath(_store);
            }
        }

        [Test]
        public void RecreateStorageStateFromOneRangeWithNonExistenceProof()
        {
            Keccak rootHash = _inputStorageTree!.RootHash;   // "..."

            AccountProofCollector accountProofCollector = new(TestItem.Tree.AccountAddress0.Bytes, new ValueKeccak[] { Keccak.Zero, TestItem.Tree.SlotsWithPaths[5].Path });
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            var proof = accountProofCollector.BuildResult();

            MemColumnsDb<StateColumns> db = new();
            DbProvider dbProvider = new(DbModeHint.Mem);
            dbProvider.RegisterDb(DbNames.State, db);
            ProgressTracker progressTracker = new(null, dbProvider.GetDb<IDb>(DbNames.State), LimboLogs.Instance);
            SnapProvider snapProvider = new(progressTracker, dbProvider, LimboLogs.Instance);

            PathWithAccount pathWithAccount = new PathWithAccount(TestItem.Tree.AccountAddress0, Build.An.Account.WithBalance(1).WithStorageRoot(rootHash).TestObject);
            AddRangeResult result = snapProvider.AddStorageRange(1, pathWithAccount, rootHash, Keccak.Zero, TestItem.Tree.SlotsWithPaths, proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray());

            Assert.That(result, Is.EqualTo(AddRangeResult.OK));
        }

        [Test]
        public void RecreateAccountStateFromOneRangeWithExistenceProof()
        {
            Keccak rootHash = _inputStorageTree!.RootHash;   // "..."

            AccountProofCollector accountProofCollector = new(TestItem.Tree.AccountAddress0.Bytes, new ValueKeccak[] { TestItem.Tree.SlotsWithPaths[0].Path, TestItem.Tree.SlotsWithPaths[5].Path });
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            var proof = accountProofCollector.BuildResult();

            MemColumnsDb<StateColumns> db = new();
            DbProvider dbProvider = new(DbModeHint.Mem);
            dbProvider.RegisterDb(DbNames.State, db);
            ProgressTracker progressTracker = new(null, dbProvider.GetDb<IDb>(DbNames.State), LimboLogs.Instance);
            SnapProvider snapProvider = new(progressTracker, dbProvider, LimboLogs.Instance);

            PathWithAccount pathWithAccount = new PathWithAccount(TestItem.Tree.AccountAddress0, Build.An.Account.WithBalance(1).WithStorageRoot(rootHash).TestObject);
            var result = snapProvider.AddStorageRange(1, pathWithAccount, rootHash, Keccak.Zero, TestItem.Tree.SlotsWithPaths, proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray());

            Assert.That(result, Is.EqualTo(AddRangeResult.OK));
        }

        [Test]
        public void RecreateStorageStateFromOneRangeWithoutProof()
        {
            Keccak rootHash = _inputStorageTree!.RootHash;   // "..."

            MemColumnsDb<StateColumns> db = new();
            DbProvider dbProvider = new(DbModeHint.Mem);
            dbProvider.RegisterDb(DbNames.State, db);
            ProgressTracker progressTracker = new(null, dbProvider.GetDb<IDb>(DbNames.State), LimboLogs.Instance);
            SnapProvider snapProvider = new(progressTracker, dbProvider, LimboLogs.Instance);

            PathWithAccount pathWithAccount = new PathWithAccount(TestItem.Tree.AccountAddress0, Build.An.Account.WithBalance(1).WithStorageRoot(rootHash).TestObject);
            var result = snapProvider.AddStorageRange(1, pathWithAccount, rootHash, TestItem.Tree.SlotsWithPaths[0].Path, TestItem.Tree.SlotsWithPaths);

            Assert.That(result, Is.EqualTo(AddRangeResult.OK));
        }

        [Test]
        public void RecreateAccountStateFromMultipleRange()
        {
            Keccak rootHash = _inputStorageTree!.RootHash;   // "..."

            // output state
            MemColumnsDb<StateColumns> db = new();
            DbProvider dbProvider = new(DbModeHint.Mem);
            dbProvider.RegisterDb(DbNames.State, db);
            ProgressTracker progressTracker = new(null, dbProvider.GetDb<IDb>(DbNames.State), LimboLogs.Instance);
            SnapProvider snapProvider = new(progressTracker, dbProvider, LimboLogs.Instance);

            AccountProofCollector accountProofCollector = new(TestItem.Tree.AccountAddress0.Bytes, new ValueKeccak[] { Keccak.Zero, TestItem.Tree.SlotsWithPaths[1].Path });
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            var proof = accountProofCollector.BuildResult();
            PathWithAccount pathWithAccount = new PathWithAccount(TestItem.Tree.AccountAddress0, Build.An.Account.WithBalance(1).WithStorageRoot(rootHash).TestObject);

            var result1 = snapProvider.AddStorageRange(1, pathWithAccount, rootHash, Keccak.Zero, TestItem.Tree.SlotsWithPaths[0..2], proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray());

            accountProofCollector = new(TestItem.Tree.AccountAddress0.Bytes, new ValueKeccak[] { TestItem.Tree.SlotsWithPaths[2].Path, TestItem.Tree.SlotsWithPaths[3].Path });
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            proof = accountProofCollector.BuildResult();

            var result2 = snapProvider.AddStorageRange(1, pathWithAccount, rootHash, TestItem.Tree.SlotsWithPaths[2].Path, TestItem.Tree.SlotsWithPaths[2..4], proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray());

            accountProofCollector = new(TestItem.Tree.AccountAddress0.Bytes, new ValueKeccak[] { TestItem.Tree.SlotsWithPaths[4].Path, TestItem.Tree.SlotsWithPaths[5].Path });
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            proof = accountProofCollector.BuildResult();

            var result3 = snapProvider.AddStorageRange(1, pathWithAccount, rootHash, TestItem.Tree.SlotsWithPaths[4].Path, TestItem.Tree.SlotsWithPaths[4..6], proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray());

            Assert.That(result1, Is.EqualTo(AddRangeResult.OK));
            Assert.That(result2, Is.EqualTo(AddRangeResult.OK));
            Assert.That(result3, Is.EqualTo(AddRangeResult.OK));
        }

        [Test]
        public void MissingAccountFromRange()
        {
            Keccak rootHash = _inputStorageTree!.RootHash;   // "..."

            // output state
            MemColumnsDb<StateColumns> db = new();
            DbProvider dbProvider = new(DbModeHint.Mem);
            dbProvider.RegisterDb(DbNames.State, db);
            ProgressTracker progressTracker = new(null, dbProvider.GetDb<IDb>(DbNames.State), LimboLogs.Instance);
            SnapProvider snapProvider = new(progressTracker, dbProvider, LimboLogs.Instance);

            AccountProofCollector accountProofCollector = new(TestItem.Tree.AccountAddress0.Bytes, new ValueKeccak[] { Keccak.Zero, TestItem.Tree.SlotsWithPaths[1].Path });
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            var proof = accountProofCollector.BuildResult();
            PathWithAccount pathWithAccount = new PathWithAccount(TestItem.Tree.AccountAddress0, Build.An.Account.WithBalance(1).WithStorageRoot(rootHash).TestObject);

            var result1 = snapProvider.AddStorageRange(1, pathWithAccount, rootHash, Keccak.Zero, TestItem.Tree.SlotsWithPaths[0..2], proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray());

            accountProofCollector = new(TestItem.Tree.AccountAddress0.Bytes, new ValueKeccak[] { TestItem.Tree.SlotsWithPaths[2].Path, TestItem.Tree.SlotsWithPaths[3].Path });
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            proof = accountProofCollector.BuildResult();

            var result2 = snapProvider.AddStorageRange(1, pathWithAccount, rootHash, TestItem.Tree.SlotsWithPaths[2].Path, TestItem.Tree.SlotsWithPaths[3..4], proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray());

            accountProofCollector = new(TestItem.Tree.AccountAddress0.Bytes, new ValueKeccak[] { TestItem.Tree.SlotsWithPaths[4].Path, TestItem.Tree.SlotsWithPaths[5].Path });
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            proof = accountProofCollector.BuildResult();

            var result3 = snapProvider.AddStorageRange(1, pathWithAccount, rootHash, TestItem.Tree.SlotsWithPaths[4].Path, TestItem.Tree.SlotsWithPaths[4..6], proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray());

            Assert.That(result1, Is.EqualTo(AddRangeResult.OK));
            Assert.That(result2, Is.EqualTo(AddRangeResult.DifferentRootHash));
            Assert.That(result3, Is.EqualTo(AddRangeResult.OK));
        }
    }
}

// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable disable

using System.Linq;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
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

namespace Nethermind.Synchronization.Test.SnapSync
{
    [TestFixture]
    public class RecreateStateFromStorageRangesTests
    {

        private TrieStore _store;
        private StateTree _inputStateTree;
        private StorageTree _inputStorageTree;
        private Hash256 _storage;

        [OneTimeSetUp]
        public void Setup()
        {
            _store = TestTrieStoreFactory.Build(new MemDb(), LimboLogs.Instance);
            (_inputStateTree, _inputStorageTree, _storage) = TestItem.Tree.GetTrees(_store);
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

            using IContainer container = new ContainerBuilder().AddModule(new TestSynchronizerModule(new TestSyncConfig())).Build();
            SnapProvider snapProvider = container.Resolve<SnapProvider>();

            var storageRange = PrepareStorageRequest(TestItem.Tree.AccountAddress0, rootHash, Keccak.Zero);
            var result = snapProvider.AddStorageRangeForAccount(storageRange, 0, TestItem.Tree.SlotsWithPaths, proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray());

            Assert.That(result, Is.EqualTo(AddRangeResult.OK));
        }

        [Test]
        public void RecreateAccountStateFromOneRangeWithExistenceProof()
        {
            Hash256 rootHash = _inputStorageTree!.RootHash;   // "..."

            AccountProofCollector accountProofCollector = new(TestItem.Tree.AccountAddress0.Bytes, new ValueHash256[] { TestItem.Tree.SlotsWithPaths[0].Path, TestItem.Tree.SlotsWithPaths[5].Path });
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            var proof = accountProofCollector.BuildResult();

            using IContainer container = new ContainerBuilder().AddModule(new TestSynchronizerModule(new TestSyncConfig())).Build();
            SnapProvider snapProvider = container.Resolve<SnapProvider>();

            var storageRange = PrepareStorageRequest(TestItem.Tree.AccountAddress0, rootHash, Keccak.Zero);
            var result = snapProvider.AddStorageRangeForAccount(storageRange, 0, TestItem.Tree.SlotsWithPaths, proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray());

            Assert.That(result, Is.EqualTo(AddRangeResult.OK));
        }

        [Test]
        public void RecreateStorageStateFromOneRangeWithoutProof()
        {
            Hash256 rootHash = _inputStorageTree!.RootHash;   // "..."

            using IContainer container = new ContainerBuilder().AddModule(new TestSynchronizerModule(new TestSyncConfig())).Build();
            SnapProvider snapProvider = container.Resolve<SnapProvider>();

            var storageRange = PrepareStorageRequest(TestItem.Tree.AccountAddress0, rootHash, TestItem.Tree.SlotsWithPaths[0].Path);
            var result = snapProvider.AddStorageRangeForAccount(storageRange, 0, TestItem.Tree.SlotsWithPaths);

            Assert.That(result, Is.EqualTo(AddRangeResult.OK));
        }

        [Test]
        public void RecreateAccountStateFromMultipleRange()
        {
            Hash256 rootHash = _inputStorageTree!.RootHash;   // "..."

            // output state
            using IContainer container = new ContainerBuilder().AddModule(new TestSynchronizerModule(new TestSyncConfig())).Build();
            SnapProvider snapProvider = container.Resolve<SnapProvider>();

            AccountProofCollector accountProofCollector = new(TestItem.Tree.AccountAddress0.Bytes, new ValueHash256[] { Keccak.Zero, TestItem.Tree.SlotsWithPaths[1].Path });
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            var proof = accountProofCollector.BuildResult();

            var storageRangeRequest = PrepareStorageRequest(TestItem.Tree.AccountAddress0, rootHash, Keccak.Zero);
            var result1 = snapProvider.AddStorageRangeForAccount(storageRangeRequest, 0, TestItem.Tree.SlotsWithPaths[0..2], proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray());

            accountProofCollector = new(TestItem.Tree.AccountAddress0.Bytes, [TestItem.Tree.SlotsWithPaths[2].Path, TestItem.Tree.SlotsWithPaths[3].Path]);
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            proof = accountProofCollector.BuildResult();

            storageRangeRequest = PrepareStorageRequest(TestItem.Tree.AccountAddress0, rootHash, TestItem.Tree.SlotsWithPaths[2].Path);
            var result2 = snapProvider.AddStorageRangeForAccount(storageRangeRequest, 0, TestItem.Tree.SlotsWithPaths[2..4], proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray());

            accountProofCollector = new(TestItem.Tree.AccountAddress0.Bytes, [TestItem.Tree.SlotsWithPaths[4].Path, TestItem.Tree.SlotsWithPaths[5].Path]);
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            proof = accountProofCollector.BuildResult();

            storageRangeRequest = PrepareStorageRequest(TestItem.Tree.AccountAddress0, rootHash, TestItem.Tree.SlotsWithPaths[4].Path);
            var result3 = snapProvider.AddStorageRangeForAccount(storageRangeRequest, 0, TestItem.Tree.SlotsWithPaths[4..6], proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray());

            Assert.That(result1, Is.EqualTo(AddRangeResult.OK));
            Assert.That(result2, Is.EqualTo(AddRangeResult.OK));
            Assert.That(result3, Is.EqualTo(AddRangeResult.OK));
        }

        [Test]
        public void MissingAccountFromRange()
        {
            Hash256 rootHash = _inputStorageTree!.RootHash;   // "..."

            // output state
            using IContainer container = new ContainerBuilder().AddModule(new TestSynchronizerModule(new TestSyncConfig())).Build();
            SnapProvider snapProvider = container.Resolve<SnapProvider>();

            AccountProofCollector accountProofCollector = new(TestItem.Tree.AccountAddress0.Bytes, new ValueHash256[] { Keccak.Zero, TestItem.Tree.SlotsWithPaths[1].Path });
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            var proof = accountProofCollector.BuildResult();

            var storageRangeRequest = PrepareStorageRequest(TestItem.Tree.AccountAddress0, rootHash, Keccak.Zero);
            var result1 = snapProvider.AddStorageRangeForAccount(storageRangeRequest, 0, TestItem.Tree.SlotsWithPaths[0..2], proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray());

            accountProofCollector = new(TestItem.Tree.AccountAddress0.Bytes, [TestItem.Tree.SlotsWithPaths[2].Path, TestItem.Tree.SlotsWithPaths[3].Path]);
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            proof = accountProofCollector.BuildResult();

            storageRangeRequest = PrepareStorageRequest(TestItem.Tree.AccountAddress0, rootHash, TestItem.Tree.SlotsWithPaths[2].Path);
            var result2 = snapProvider.AddStorageRangeForAccount(storageRangeRequest, 0, TestItem.Tree.SlotsWithPaths[3..4], proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray());

            accountProofCollector = new(TestItem.Tree.AccountAddress0.Bytes, [TestItem.Tree.SlotsWithPaths[4].Path, TestItem.Tree.SlotsWithPaths[5].Path]);
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            proof = accountProofCollector.BuildResult();

            storageRangeRequest = PrepareStorageRequest(TestItem.Tree.AccountAddress0, rootHash, TestItem.Tree.SlotsWithPaths[4].Path);
            var result3 = snapProvider.AddStorageRangeForAccount(storageRangeRequest, 0, TestItem.Tree.SlotsWithPaths[4..6], proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray());

            Assert.That(result1, Is.EqualTo(AddRangeResult.OK));
            Assert.That(result2, Is.EqualTo(AddRangeResult.DifferentRootHash));
            Assert.That(result3, Is.EqualTo(AddRangeResult.OK));
        }

        private static StorageRange PrepareStorageRequest(ValueHash256 accountPath, Hash256 storageRoot, ValueHash256 startingHash)
        {
            return new StorageRange()
            {
                StartingHash = startingHash,
                Accounts = new ArrayPoolList<PathWithAccount>(1) { new(accountPath, new Account(UInt256.Zero).WithChangedStorageRoot(storageRoot)) }
            };
        }
    }
}

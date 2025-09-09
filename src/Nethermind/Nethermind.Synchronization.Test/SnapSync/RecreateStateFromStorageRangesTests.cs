// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable disable

using System;
using System.Linq;
using Autofac;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Proofs;
using Nethermind.State.Snap;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.SnapSync
{
    [TestFixture]
    public class RecreateStateFromStorageRangesTests
    {

        private TestRawTrieStore _store;
        private StateTree _inputStateTree;
        private StorageTree _inputStorageTree;
        private Hash256 _storage;

        [OneTimeSetUp]
        public void Setup()
        {
            _store = new TestRawTrieStore(new MemDb());
            (_inputStateTree, _inputStorageTree, _storage) = TestItem.Tree.GetTrees(_store);
        }

        [OneTimeTearDown]
        public void TearDown() => ((IDisposable)_store)?.Dispose();

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

        [Test]
        public void AddStorageRange_WhereProofIsTheSameAsAllKey_ShouldStillStore()
        {
            Hash256 account = TestItem.KeccakA;
            TestMemDb testMemDb = new();
            var rawTrieStore = new RawScopedTrieStore(new NodeStorage(testMemDb), account);
            StorageTree tree = new(rawTrieStore, LimboLogs.Instance);

            (AddRangeResult result, bool moreChildrenToRight) = SnapProviderHelper.AddStorageRange(
                tree,
                new PathWithAccount(account, new Account(1, 1, new Hash256("0xeb8594ba5b3314111518b584bbd3801fb3aed5970bd8b47fd9ff744505fe101c"), TestItem.KeccakA)),
                [
                    new PathWithStorageSlot(new ValueHash256("0x290decd9548b62a8d60345a988386fc84ba6bc95484008f6362f93160ef3e563"), Bytes.FromHexString("94654f75e491acf8c380d2a6906e67e2e56813665e")),
                ],
                Keccak.Zero,
                null,
                proofs: [
                    Bytes.FromHexString("f838a120290decd9548b62a8d60345a988386fc84ba6bc95484008f6362f93160ef3e5639594654f75e491acf8c380d2a6906e67e2e56813665e"),
                    Bytes.FromHexString("f838a120290decd9548b62a8d60345a988386fc84ba6bc95484008f6362f93160ef3e5639594654f75e491acf8c380d2a6906e67e2e56813665e"),
                ]);

            result.Should().Be(AddRangeResult.OK);
            moreChildrenToRight.Should().BeFalse();
            testMemDb.WritesCount.Should().Be(1);
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

// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Paprika;
using Nethermind.Serialization.Rlp;
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
    public class RecreateStateFromStorageRangesTestsPaprika
    {
        private TrieStore _store;
        private StateTree _inputStateTree;
        private StorageTree _inputStorageTree;
        private Hash256 _storage;

        private PathWithAccount _pathWithAccount = new PathWithAccount(TestItem.Tree.AccountAddress0.ValueHash256, new Account(UInt256.Zero));

        [OneTimeSetUp]
        public void Setup()
        {
            _store = new TrieStore(new MemDb(), LimboLogs.Instance);
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

            MemDb db = new();
            IDbProvider dbProvider = new DbProvider();
            dbProvider.RegisterDb(DbNames.State, db);
            IStateFactory stateFactory = new PaprikaStateFactory();
            ProgressTracker progressTracker = new(null, dbProvider.StateDb, stateFactory, LimboLogs.Instance);
            SnapProvider snapProvider = new(progressTracker, dbProvider.StateDb, new NodeStorage(db), LimboLogs.Instance);

            IRawState rawState = progressTracker.GetSyncState();
            rawState.SetAccount(TestItem.Tree.AccountsWithPaths[0].Path, TestItem.Tree.AccountsWithPaths[0].Account);
            rawState.Commit(true);

            var result = snapProvider.AddStorageRange(1, TestItem.Tree.AccountsWithPaths[0], rootHash, Keccak.Zero, TestItem.Tree.SlotsWithPaths[0..6], proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray());

            Assert.That(result, Is.EqualTo(AddRangeResult.OK));

            rawState.Finalize(1);
            IReadOnlyState state = stateFactory.GetReadOnly(rawState.StateRoot);
            AssertAllStorageSlots(state, 6);
        }

        [Test]
        public void RecreateAccountStateFromOneRangeWithExistenceProof()
        {
            Hash256 rootHash = _inputStorageTree!.RootHash;   // "..."

            AccountProofCollector accountProofCollector = new(TestItem.Tree.AccountAddress0.Bytes, new ValueHash256[] { TestItem.Tree.SlotsWithPaths[0].Path, TestItem.Tree.SlotsWithPaths[5].Path });
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            var proof = accountProofCollector.BuildResult();

            MemDb db = new();
            IDbProvider dbProvider = new DbProvider();
            dbProvider.RegisterDb(DbNames.State, db);
            IStateFactory stateFactory = new PaprikaStateFactory();
            ProgressTracker progressTracker = new(null, dbProvider.StateDb, stateFactory, LimboLogs.Instance);
            SnapProvider snapProvider = new(progressTracker, dbProvider.StateDb, new NodeStorage(db), LimboLogs.Instance);

            IRawState rawState = progressTracker.GetSyncState();
            rawState.SetAccount(TestItem.Tree.AccountsWithPaths[0].Path, TestItem.Tree.AccountsWithPaths[0].Account);
            rawState.Commit(true);

            var result = snapProvider.AddStorageRange(1, TestItem.Tree.AccountsWithPaths[0], rootHash, Keccak.Zero, TestItem.Tree.SlotsWithPaths[0..6], proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray());

            Assert.That(result, Is.EqualTo(AddRangeResult.OK));

            rawState.Finalize(1);
            IReadOnlyState state = stateFactory.GetReadOnly(rawState.StateRoot);
            AssertAllStorageSlots(state, 6);
        }

        [Test]
        public void RecreateStorageStateFromOneRangeWithoutProof()
        {
            Hash256 rootHash = _inputStorageTree!.RootHash;   // "..."

            MemDb db = new();
            IDbProvider dbProvider = new DbProvider();
            dbProvider.RegisterDb(DbNames.State, db);
            IStateFactory stateFactory = new PaprikaStateFactory();
            ProgressTracker progressTracker = new(null, dbProvider.StateDb, stateFactory, LimboLogs.Instance);
            SnapProvider snapProvider = new(progressTracker, dbProvider.StateDb, new NodeStorage(db), LimboLogs.Instance);

            IRawState rawState = progressTracker.GetSyncState();
            rawState.SetAccount(TestItem.Tree.AccountsWithPaths[0].Path, TestItem.Tree.AccountsWithPaths[0].Account);
            rawState.Commit(true);

            var result = snapProvider.AddStorageRange(1, TestItem.Tree.AccountsWithPaths[0], rootHash, TestItem.Tree.SlotsWithPaths[0].Path, TestItem.Tree.SlotsWithPaths[0..6]);

            Assert.That(result, Is.EqualTo(AddRangeResult.OK));
            rawState.Finalize(1);
            IReadOnlyState state = stateFactory.GetReadOnly(rawState.StateRoot);
            AssertAllStorageSlots(state, 6);
        }

        [Test]
        public void RecreateAccountStateFromMultipleRange()
        {
            Hash256 rootHash = _inputStorageTree!.RootHash;   // "..."

            // output state
            MemDb db = new();
            IDbProvider dbProvider = new DbProvider();
            dbProvider.RegisterDb(DbNames.State, db);
            IStateFactory stateFactory = new PaprikaStateFactory();
            ProgressTracker progressTracker = new(null, dbProvider.StateDb, stateFactory, LimboLogs.Instance);
            SnapProvider snapProvider = new(progressTracker, dbProvider.StateDb, new NodeStorage(db), LimboLogs.Instance);

            StorageTree storageTree =
                new StorageTree(new ScopedTrieStore(new TrieStore(new MemDb(), NullLogManager.Instance), null),
                    NullLogManager.Instance);

            storageTree.Set(TestItem.Tree.SlotsWithPaths[0].Path, TestItem.Tree.SlotsWithPaths[0].SlotRlpValue, false);
            storageTree.Set(TestItem.Tree.SlotsWithPaths[1].Path, TestItem.Tree.SlotsWithPaths[1].SlotRlpValue, false);
            storageTree.Set(TestItem.Tree.SlotsWithPaths[2].Path, TestItem.Tree.SlotsWithPaths[2].SlotRlpValue, false);
            storageTree.Set(TestItem.Tree.SlotsWithPaths[3].Path, TestItem.Tree.SlotsWithPaths[3].SlotRlpValue, false);
            storageTree.Set(TestItem.Tree.SlotsWithPaths[4].Path, TestItem.Tree.SlotsWithPaths[4].SlotRlpValue, false);
            storageTree.Set(TestItem.Tree.SlotsWithPaths[5].Path, TestItem.Tree.SlotsWithPaths[5].SlotRlpValue, false);

            storageTree.UpdateRootHash();

            PathWithAccount pwa = TestItem.Tree.AccountsWithPaths[0];

            IRawState rawState = progressTracker.GetSyncState();
            rawState.SetAccount(pwa.Path, Build.An.Account.WithBalance(1).WithStorageRoot(rootHash).TestObject);
            rawState.Commit(true);

            AccountProofCollector accountProofCollector = new(TestItem.Tree.AccountAddress0.Bytes, new ValueHash256[] { Keccak.Zero, TestItem.Tree.SlotsWithPaths[1].Path });
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            var proof = accountProofCollector.BuildResult();

            var result1 = snapProvider.AddStorageRange(1, pwa, rootHash, Keccak.Zero, TestItem.Tree.SlotsWithPaths[0..2], proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray());

            accountProofCollector = new(TestItem.Tree.AccountAddress0.Bytes, new ValueHash256[] { TestItem.Tree.SlotsWithPaths[2].Path, TestItem.Tree.SlotsWithPaths[3].Path });
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            proof = accountProofCollector.BuildResult();

            var result2 = snapProvider.AddStorageRange(1, pwa, rootHash, TestItem.Tree.SlotsWithPaths[2].Path, TestItem.Tree.SlotsWithPaths[2..4], proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray());

            accountProofCollector = new(TestItem.Tree.AccountAddress0.Bytes, new ValueHash256[] { TestItem.Tree.SlotsWithPaths[4].Path, TestItem.Tree.SlotsWithPaths[5].Path });
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            proof = accountProofCollector.BuildResult();

            var result3 = snapProvider.AddStorageRange(1, pwa, rootHash, TestItem.Tree.SlotsWithPaths[4].Path, TestItem.Tree.SlotsWithPaths[4..6], proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray());

            Assert.That(result1, Is.EqualTo(AddRangeResult.OK));
            Assert.That(result2, Is.EqualTo(AddRangeResult.OK));
            Assert.That(result3, Is.EqualTo(AddRangeResult.OK));

            rawState.Finalize(1);

            var hasRoot = stateFactory.HasRoot(_inputStateTree.RootHash);

            IReadOnlyState state = stateFactory.GetReadOnly(_inputStateTree.RootHash);
            AssertAllStorageSlots(state, 6);
        }

        [Test]
        public void RecreateAccountStateFromMultipleRange_PivotChange()
        {
            Hash256 rootHash = _inputStorageTree!.RootHash;   // "..."

            // output state
            MemDb db = new();
            IDbProvider dbProvider = new DbProvider();
            dbProvider.RegisterDb(DbNames.State, db);
            IStateFactory stateFactory = new PaprikaStateFactory();
            ProgressTracker progressTracker = new(null, dbProvider.StateDb, stateFactory, LimboLogs.Instance);
            SnapProvider snapProvider = new(progressTracker, dbProvider.StateDb, new NodeStorage(db), LimboLogs.Instance);

            PathWithAccount pwa = TestItem.Tree.AccountsWithPaths[0];

            IRawState rawState = progressTracker.GetSyncState();
            rawState.SetAccount(pwa.Path, pwa.Account);
            rawState.Commit(true);

            AccountProofCollector accountProofCollector = new(TestItem.Tree.AccountAddress0.Bytes, new ValueHash256[] { Keccak.Zero, TestItem.Tree.SlotsWithPaths[1].Path });
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            var proof = accountProofCollector.BuildResult();

            var result1 = snapProvider.AddStorageRange(1, pwa, rootHash, Keccak.Zero, TestItem.Tree.SlotsWithPaths[0..2], proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray());

            _inputStorageTree.Set(TestItem.Tree.SlotsWithPaths[6].Path, TestItem.Tree.SlotsWithPaths[6].SlotRlpValue, false);
            _inputStorageTree.Commit(1);
            pwa.Account = pwa.Account.WithChangedStorageRoot(_inputStorageTree.RootHash);
            _inputStateTree.Set(pwa.Path, pwa.Account);
            _inputStateTree.Commit(1);
            Hash256 newRootHash = _inputStorageTree.RootHash;

            accountProofCollector = new(TestItem.Tree.AccountAddress0.Bytes, new ValueHash256[] { TestItem.Tree.SlotsWithPaths[2].Path, TestItem.Tree.SlotsWithPaths[3].Path });
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            proof = accountProofCollector.BuildResult();

            var result2 = snapProvider.AddStorageRange(1, pwa, newRootHash, TestItem.Tree.SlotsWithPaths[2].Path, TestItem.Tree.SlotsWithPaths[2..4], proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray());

            accountProofCollector = new(TestItem.Tree.AccountAddress0.Bytes, new ValueHash256[] { TestItem.Tree.SlotsWithPaths[4].Path, TestItem.Tree.SlotsWithPaths[5].Path });
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            proof = accountProofCollector.BuildResult();

            var result3 = snapProvider.AddStorageRange(1, pwa, newRootHash, TestItem.Tree.SlotsWithPaths[4].Path, TestItem.Tree.SlotsWithPaths[4..6], proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray());

            accountProofCollector = new(TestItem.Tree.AccountAddress0.Bytes, new ValueHash256[] { TestItem.Tree.SlotsWithPaths[6].Path});
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            proof = accountProofCollector.BuildResult();

            var result4 = snapProvider.AddStorageRange(1, pwa, newRootHash, TestItem.Tree.SlotsWithPaths[6].Path,
                TestItem.Tree.SlotsWithPaths[6..7], proof!.StorageProofs.SelectMany(sp => sp.Proof).ToArray());
            Assert.That(result1, Is.EqualTo(AddRangeResult.OK));
            Assert.That(result2, Is.EqualTo(AddRangeResult.OK));
            Assert.That(result3, Is.EqualTo(AddRangeResult.OK));
            Assert.That(result4, Is.EqualTo(AddRangeResult.OK));

            rawState.Finalize(1);

            IReadOnlyState state = stateFactory.GetReadOnly(rawState.StateRoot);
            AssertAllStorageSlots(state, TestItem.Tree.SlotsWithPaths.Length);
        }

        [Test]
        public void RecreateAccountStateFromMultipleRange_PivotChange_Low()
        {
            Hash256 rootHash = _inputStorageTree!.RootHash;   // "..."

            // output state
            MemDb db = new();
            IDbProvider dbProvider = new DbProvider();
            dbProvider.RegisterDb(DbNames.State, db);
            IStateFactory stateFactory = new PaprikaStateFactory();
            ProgressTracker progressTracker = new(null, dbProvider.StateDb, stateFactory, LimboLogs.Instance);
            SnapProvider snapProvider = new(progressTracker, dbProvider.StateDb, new NodeStorage(db), LimboLogs.Instance);

            PathWithAccount pwa = TestItem.Tree.AccountsWithPaths[0];

            IRawState rawState = progressTracker.GetSyncState();
            rawState.SetAccount(pwa.Path, pwa.Account);
            rawState.Commit(true);

            AccountProofCollector accountProofCollector = new(TestItem.Tree.AccountAddress0.Bytes, new ValueHash256[] { Keccak.Zero, TestItem.Tree.SlotsWithPaths[1].Path });
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            var proof = accountProofCollector.BuildResult();

            var result1 = snapProvider.AddStorageRange(1, pwa, rootHash, Keccak.Zero, TestItem.Tree.SlotsWithPaths[0..2], proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray());

            accountProofCollector = new(TestItem.Tree.AccountAddress0.Bytes, new ValueHash256[] { TestItem.Tree.SlotsWithPaths[2].Path, TestItem.Tree.SlotsWithPaths[3].Path });
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            proof = accountProofCollector.BuildResult();

            var result2 = snapProvider.AddStorageRange(1, pwa, rootHash, TestItem.Tree.SlotsWithPaths[2].Path, TestItem.Tree.SlotsWithPaths[2..4], proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray());


            _inputStorageTree.Set(TestItem.Tree.SlotsWithPaths[0].Path, [0], false);
            _inputStorageTree.Commit(1);
            pwa.Account = pwa.Account.WithChangedStorageRoot(_inputStorageTree.RootHash);
            _inputStateTree.Set(pwa.Path, pwa.Account);
            _inputStateTree.Commit(1);
            Hash256 newRootHash = _inputStorageTree.RootHash;


            accountProofCollector = new(TestItem.Tree.AccountAddress0.Bytes, new ValueHash256[] { TestItem.Tree.SlotsWithPaths[4].Path, TestItem.Tree.SlotsWithPaths[5].Path });
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            proof = accountProofCollector.BuildResult();

            var result3 = snapProvider.AddStorageRange(1, pwa, newRootHash, TestItem.Tree.SlotsWithPaths[4].Path, TestItem.Tree.SlotsWithPaths[4..6], proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray());

            Assert.That(result1, Is.EqualTo(AddRangeResult.OK));
            Assert.That(result2, Is.EqualTo(AddRangeResult.OK));
            Assert.That(result3, Is.EqualTo(AddRangeResult.OK));

            rawState.Finalize(1);

            IReadOnlyState state = stateFactory.GetReadOnly(rawState.StateRoot);
            AssertAllStorageSlots(state, 6);
        }

        [Test]
        public void MissingAccountFromRange()
        {
            Hash256 rootHash = _inputStorageTree!.RootHash;   // "..."

            // output state
            MemDb db = new();
            IDbProvider dbProvider = new DbProvider();
            dbProvider.RegisterDb(DbNames.State, db);
            IStateFactory stateFactory = new PaprikaStateFactory();
            ProgressTracker progressTracker = new(null, dbProvider.StateDb, stateFactory, LimboLogs.Instance);
            SnapProvider snapProvider = new(progressTracker, dbProvider.StateDb, new NodeStorage(db), LimboLogs.Instance);

            PathWithAccount pwa = TestItem.Tree.AccountsWithPaths[0];

            IRawState rawState = progressTracker.GetSyncState();
            rawState.SetAccount(pwa.Path, pwa.Account);
            rawState.Commit(true);

            AccountProofCollector accountProofCollector = new(TestItem.Tree.AccountAddress0.Bytes, new ValueHash256[] { Keccak.Zero, TestItem.Tree.SlotsWithPaths[1].Path });
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            var proof = accountProofCollector.BuildResult();

            var result1 = snapProvider.AddStorageRange(1, pwa, rootHash, Keccak.Zero, TestItem.Tree.SlotsWithPaths[0..2], proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray());

            accountProofCollector = new(TestItem.Tree.AccountAddress0.Bytes, new ValueHash256[] { TestItem.Tree.SlotsWithPaths[2].Path, TestItem.Tree.SlotsWithPaths[3].Path });
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            proof = accountProofCollector.BuildResult();

            var result2 = snapProvider.AddStorageRange(1, pwa, rootHash, TestItem.Tree.SlotsWithPaths[2].Path, TestItem.Tree.SlotsWithPaths[3..4], proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray());

            accountProofCollector = new(TestItem.Tree.AccountAddress0.Bytes, new ValueHash256[] { TestItem.Tree.SlotsWithPaths[4].Path, TestItem.Tree.SlotsWithPaths[5].Path });
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            proof = accountProofCollector.BuildResult();

            var result3 = snapProvider.AddStorageRange(1, pwa, rootHash, TestItem.Tree.SlotsWithPaths[4].Path, TestItem.Tree.SlotsWithPaths[4..6], proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray());

            Assert.That(result1, Is.EqualTo(AddRangeResult.OK));
            Assert.That(result2, Is.EqualTo(AddRangeResult.DifferentRootHash));
            Assert.That(result3, Is.EqualTo(AddRangeResult.OK));
        }

        private SnapProvider CreateSnapProvider(ProgressTracker progressTracker, IDbProvider dbProvider)
        {
            try
            {
                IDb _ = dbProvider.CodeDb;
            }
            catch (ArgumentException)
            {
                dbProvider.RegisterDb(DbNames.Code, new MemDb());
            }
            return new(progressTracker, dbProvider.CodeDb, new NodeStorage(dbProvider.StateDb), LimboLogs.Instance);
        }

        private static void AssertAllStorageSlots(IReadOnlyState state, int upTo)
        {
            foreach (var slotItem in TestItem.Tree.SlotsWithPaths[0..upTo])
            {
                var dataAtSlot = state.GetStorageAt(TestItem.Tree.AccountAddress0, slotItem.Path);
                var rlpContext = new Rlp.ValueDecoderContext(slotItem.SlotRlpValue);

                Assert.That(dataAtSlot.ToArray(), Is.EqualTo(rlpContext.DecodeByteArray()));
            }
        }
    }
}

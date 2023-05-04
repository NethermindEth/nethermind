// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable disable

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
using Nethermind.State.Snap;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Store.Test
{
    [TestFixture]
    public class RecreateStateFromAccountRangesTests
    {
        private StateTree _inputTree;

        [OneTimeSetUp]
        public void Setup()
        {
            _inputTree = TestItem.Tree.GetStateTree(null);
        }

        //[Test]
        public void Test01()
        {
            Keccak rootHash = _inputTree.RootHash;   // "0x8c81279168edc449089449bc0f2136fc72c9645642845755633cf259cd97988b"

            AccountProofCollector accountProofCollector = new(TestItem.Tree.AccountsWithPaths[0].Path.Bytes);
            _inputTree.Accept(accountProofCollector, _inputTree.RootHash);
            byte[][] firstProof = accountProofCollector.BuildResult().Proof;

            accountProofCollector = new(TestItem.Tree.AccountsWithPaths[5].Path.Bytes);
            _inputTree.Accept(accountProofCollector, _inputTree.RootHash);
            byte[][] lastProof = accountProofCollector.BuildResult().Proof;

            MemDb db = new();
            TrieStore store = new(db, LimboLogs.Instance);
            StateTree tree = new(store, LimboLogs.Instance);

            IList<TrieNode> nodes = new List<TrieNode>();

            for (int i = 0; i < (firstProof!).Length; i++)
            {
                byte[] nodeBytes = (firstProof!)[i];
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
                byte[] nodeBytes = (lastProof!)[i];
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

            tree.Set(TestItem.Tree.AccountsWithPaths[0].Path, TestItem.Tree.AccountsWithPaths[0].Account);
            tree.Set(TestItem.Tree.AccountsWithPaths[1].Path, TestItem.Tree.AccountsWithPaths[1].Account);
            tree.Set(TestItem.Tree.AccountsWithPaths[2].Path, TestItem.Tree.AccountsWithPaths[2].Account);
            tree.Set(TestItem.Tree.AccountsWithPaths[3].Path, TestItem.Tree.AccountsWithPaths[3].Account);
            tree.Set(TestItem.Tree.AccountsWithPaths[4].Path, TestItem.Tree.AccountsWithPaths[4].Account);
            tree.Set(TestItem.Tree.AccountsWithPaths[5].Path, TestItem.Tree.AccountsWithPaths[5].Account);

            tree.Commit(0);

            Assert.That(tree.RootHash, Is.EqualTo(_inputTree.RootHash));
            Assert.That(db.Keys.Count, Is.EqualTo(6));  // we don't persist proof nodes (boundary nodes)
            Assert.IsFalse(db.KeyExists(rootHash)); // the root node is a part of the proof nodes
        }

        [Test]
        public void RecreateAccountStateFromOneRangeWithNonExistenceProof()
        {
            Keccak rootHash = _inputTree.RootHash;   // "0x8c81279168edc449089449bc0f2136fc72c9645642845755633cf259cd97988b"

            AccountProofCollector accountProofCollector = new(Keccak.Zero.Bytes);
            _inputTree.Accept(accountProofCollector, _inputTree.RootHash);
            byte[][] firstProof = accountProofCollector.BuildResult().Proof;
            accountProofCollector = new(TestItem.Tree.AccountsWithPaths[5].Path.Bytes);
            _inputTree.Accept(accountProofCollector, _inputTree.RootHash);
            byte[][] lastProof = accountProofCollector.BuildResult().Proof;

            MemDb db = new();
            DbProvider dbProvider = new(DbModeHint.Mem);
            dbProvider.RegisterDb(DbNames.State, db);
            ProgressTracker progressTracker = new(null, dbProvider.GetDb<IDb>(DbNames.State), LimboLogs.Instance);
            SnapProvider snapProvider = new(progressTracker, dbProvider, LimboLogs.Instance);
            AddRangeResult result = snapProvider.AddAccountRange(1, rootHash, Keccak.Zero, TestItem.Tree.AccountsWithPaths, firstProof!.Concat(lastProof!).ToArray());

            Assert.That(result, Is.EqualTo(AddRangeResult.OK));
            Assert.That(db.Keys.Count, Is.EqualTo(10));  // we persist proof nodes (boundary nodes) via stitching
            Assert.IsFalse(db.KeyExists(rootHash));
        }

        [Test]
        public void RecreateAccountStateFromOneRangeWithExistenceProof()
        {
            Keccak rootHash = _inputTree.RootHash;   // "0x8c81279168edc449089449bc0f2136fc72c9645642845755633cf259cd97988b"

            AccountProofCollector accountProofCollector = new(TestItem.Tree.AccountsWithPaths[0].Path.Bytes);
            _inputTree.Accept(accountProofCollector, _inputTree.RootHash);
            byte[][] firstProof = accountProofCollector.BuildResult().Proof;
            accountProofCollector = new(TestItem.Tree.AccountsWithPaths[5].Path.Bytes);
            _inputTree.Accept(accountProofCollector, _inputTree.RootHash);
            byte[][] lastProof = accountProofCollector.BuildResult().Proof;

            MemDb db = new();
            DbProvider dbProvider = new(DbModeHint.Mem);
            dbProvider.RegisterDb(DbNames.State, db);
            ProgressTracker progressTracker = new(null, dbProvider.GetDb<IDb>(DbNames.State), LimboLogs.Instance);
            SnapProvider snapProvider = new(progressTracker, dbProvider, LimboLogs.Instance);
            var result = snapProvider.AddAccountRange(1, rootHash, TestItem.Tree.AccountsWithPaths[0].Path, TestItem.Tree.AccountsWithPaths, firstProof!.Concat(lastProof!).ToArray());

            Assert.That(result, Is.EqualTo(AddRangeResult.OK));
            Assert.That(db.Keys.Count, Is.EqualTo(10));  // we persist proof nodes (boundary nodes) via stitching
            Assert.IsFalse(db.KeyExists(rootHash));
        }

        [Test]
        public void RecreateAccountStateFromOneRangeWithoutProof()
        {
            Keccak rootHash = _inputTree.RootHash;   // "0x8c81279168edc449089449bc0f2136fc72c9645642845755633cf259cd97988b"

            MemDb db = new();
            DbProvider dbProvider = new(DbModeHint.Mem);
            dbProvider.RegisterDb(DbNames.State, db);
            ProgressTracker progressTracker = new(null, dbProvider.GetDb<IDb>(DbNames.State), LimboLogs.Instance);
            SnapProvider snapProvider = new(progressTracker, dbProvider, LimboLogs.Instance);
            var result = snapProvider.AddAccountRange(1, rootHash, TestItem.Tree.AccountsWithPaths[0].Path, TestItem.Tree.AccountsWithPaths);

            Assert.That(result, Is.EqualTo(AddRangeResult.OK));
            Assert.That(db.Keys.Count, Is.EqualTo(10));  // we don't have the proofs so we persist all nodes
            Assert.IsFalse(db.KeyExists(rootHash)); // the root node is NOT a part of the proof nodes
        }

        [Test]
        public void RecreateAccountStateFromMultipleRange()
        {
            Keccak rootHash = _inputTree.RootHash;   // "0x8c81279168edc449089449bc0f2136fc72c9645642845755633cf259cd97988b"

            // output state
            MemDb db = new();
            DbProvider dbProvider = new(DbModeHint.Mem);
            dbProvider.RegisterDb(DbNames.State, db);
            ProgressTracker progressTracker = new(null, dbProvider.GetDb<IDb>(DbNames.State), LimboLogs.Instance);
            SnapProvider snapProvider = new(progressTracker, dbProvider, LimboLogs.Instance);

            AccountProofCollector accountProofCollector = new(Keccak.Zero.Bytes);
            _inputTree.Accept(accountProofCollector, _inputTree.RootHash);
            byte[][] firstProof = accountProofCollector.BuildResult().Proof;
            accountProofCollector = new(TestItem.Tree.AccountsWithPaths[1].Path.Bytes);
            _inputTree.Accept(accountProofCollector, _inputTree.RootHash);
            byte[][] lastProof = accountProofCollector.BuildResult().Proof;

            var result1 = snapProvider.AddAccountRange(1, rootHash, Keccak.Zero, TestItem.Tree.AccountsWithPaths[0..2], firstProof!.Concat(lastProof!).ToArray());

            Assert.That(db.Keys.Count, Is.EqualTo(2));

            accountProofCollector = new(TestItem.Tree.AccountsWithPaths[2].Path.Bytes);
            _inputTree.Accept(accountProofCollector, _inputTree.RootHash);
            firstProof = accountProofCollector.BuildResult().Proof;
            accountProofCollector = new(TestItem.Tree.AccountsWithPaths[3].Path.Bytes);
            _inputTree.Accept(accountProofCollector, _inputTree.RootHash);
            lastProof = accountProofCollector.BuildResult().Proof;

            var result2 = snapProvider.AddAccountRange(1, rootHash, TestItem.Tree.AccountsWithPaths[2].Path, TestItem.Tree.AccountsWithPaths[2..4], firstProof!.Concat(lastProof!).ToArray());

            Assert.That(db.Keys.Count, Is.EqualTo(5));  // we don't persist proof nodes (boundary nodes)

            accountProofCollector = new(TestItem.Tree.AccountsWithPaths[4].Path.Bytes);
            _inputTree.Accept(accountProofCollector, _inputTree.RootHash);
            firstProof = accountProofCollector.BuildResult().Proof;
            accountProofCollector = new(TestItem.Tree.AccountsWithPaths[5].Path.Bytes);
            _inputTree.Accept(accountProofCollector, _inputTree.RootHash);
            lastProof = accountProofCollector.BuildResult().Proof;

            var result3 = snapProvider.AddAccountRange(1, rootHash, TestItem.Tree.AccountsWithPaths[4].Path, TestItem.Tree.AccountsWithPaths[4..6], firstProof!.Concat(lastProof!).ToArray());

            Assert.That(result1, Is.EqualTo(AddRangeResult.OK));
            Assert.That(result2, Is.EqualTo(AddRangeResult.OK));
            Assert.That(result3, Is.EqualTo(AddRangeResult.OK));
            Assert.That(db.Keys.Count, Is.EqualTo(10));  // we persist proof nodes (boundary nodes) via stitching
            Assert.IsFalse(db.KeyExists(rootHash));
        }

        [Test]
        public void MissingAccountFromRange()
        {
            Keccak rootHash = _inputTree.RootHash;   // "0x8c81279168edc449089449bc0f2136fc72c9645642845755633cf259cd97988b"

            // output state
            MemDb db = new();
            DbProvider dbProvider = new(DbModeHint.Mem);
            dbProvider.RegisterDb(DbNames.State, db);
            ProgressTracker progressTracker = new(null, dbProvider.GetDb<IDb>(DbNames.State), LimboLogs.Instance);
            SnapProvider snapProvider = new(progressTracker, dbProvider, LimboLogs.Instance);

            AccountProofCollector accountProofCollector = new(Keccak.Zero.Bytes);
            _inputTree.Accept(accountProofCollector, _inputTree.RootHash);
            byte[][] firstProof = accountProofCollector.BuildResult().Proof;
            accountProofCollector = new(TestItem.Tree.AccountsWithPaths[1].Path.Bytes);
            _inputTree.Accept(accountProofCollector, _inputTree.RootHash);
            byte[][] lastProof = accountProofCollector.BuildResult().Proof;

            var result1 = snapProvider.AddAccountRange(1, rootHash, Keccak.Zero, TestItem.Tree.AccountsWithPaths[0..2], firstProof!.Concat(lastProof!).ToArray());

            Assert.That(db.Keys.Count, Is.EqualTo(2));

            accountProofCollector = new(TestItem.Tree.AccountsWithPaths[2].Path.Bytes);
            _inputTree.Accept(accountProofCollector, _inputTree.RootHash);
            firstProof = accountProofCollector.BuildResult().Proof;
            accountProofCollector = new(TestItem.Tree.AccountsWithPaths[3].Path.Bytes);
            _inputTree.Accept(accountProofCollector, _inputTree.RootHash);
            lastProof = accountProofCollector.BuildResult().Proof;

            // missing TestItem.Tree.AccountsWithHashes[2]
            var result2 = snapProvider.AddAccountRange(1, rootHash, TestItem.Tree.AccountsWithPaths[2].Path, TestItem.Tree.AccountsWithPaths[3..4], firstProof!.Concat(lastProof!).ToArray());

            Assert.That(db.Keys.Count, Is.EqualTo(2));

            accountProofCollector = new(TestItem.Tree.AccountsWithPaths[4].Path.Bytes);
            _inputTree.Accept(accountProofCollector, _inputTree.RootHash);
            firstProof = accountProofCollector.BuildResult().Proof;
            accountProofCollector = new(TestItem.Tree.AccountsWithPaths[5].Path.Bytes);
            _inputTree.Accept(accountProofCollector, _inputTree.RootHash);
            lastProof = accountProofCollector.BuildResult().Proof;

            var result3 = snapProvider.AddAccountRange(1, rootHash, TestItem.Tree.AccountsWithPaths[4].Path, TestItem.Tree.AccountsWithPaths[4..6], firstProof!.Concat(lastProof!).ToArray());

            Assert.That(result1, Is.EqualTo(AddRangeResult.OK));
            Assert.That(result2, Is.EqualTo(AddRangeResult.DifferentRootHash));
            Assert.That(result3, Is.EqualTo(AddRangeResult.OK));
            Assert.That(db.Keys.Count, Is.EqualTo(6));
            Assert.IsFalse(db.KeyExists(rootHash));
        }
    }
}

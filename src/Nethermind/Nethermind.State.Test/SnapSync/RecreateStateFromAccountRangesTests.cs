// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable disable

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Proofs;
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

        private byte[][] CreateProofForPath(byte[] path)
        {
            AccountProofCollector accountProofCollector = new(path);
            _inputTree.Accept(accountProofCollector, _inputTree.RootHash);
            return accountProofCollector.BuildResult().Proof;
        }

        //[Test]
        public void Test01()
        {
            Keccak rootHash = _inputTree.RootHash;   // "0x8c81279168edc449089449bc0f2136fc72c9645642845755633cf259cd97988b"

            byte[][] firstProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[0].Path.Bytes);
            byte[][] lastProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[5].Path.Bytes);

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

            Assert.AreEqual(_inputTree.RootHash, tree.RootHash);
            Assert.AreEqual(6, db.Keys.Count);  // we don't persist proof nodes (boundary nodes)
            Assert.IsFalse(db.KeyExists(rootHash)); // the root node is a part of the proof nodes
        }

        [Test]
        public void RecreateAccountStateFromOneRangeWithNonExistenceProof()
        {
            Keccak rootHash = _inputTree.RootHash;   // "0x8c81279168edc449089449bc0f2136fc72c9645642845755633cf259cd97988b"

            byte[][] firstProof = CreateProofForPath(Keccak.Zero.Bytes);
            byte[][] lastProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[5].Path.Bytes);

            MemDb db = new();
            DbProvider dbProvider = new(DbModeHint.Mem);
            dbProvider.RegisterDb(DbNames.State, db);
            ProgressTracker progressTracker = new(null, dbProvider.GetDb<IDb>(DbNames.State), LimboLogs.Instance);
            SnapProvider snapProvider = new(progressTracker, dbProvider, LimboLogs.Instance);
            AddRangeResult result = snapProvider.AddAccountRange(1, rootHash, Keccak.Zero, TestItem.Tree.AccountsWithPaths, firstProof!.Concat(lastProof!).ToArray());

            Assert.AreEqual(AddRangeResult.OK, result);
            Assert.AreEqual(10, db.Keys.Count);  // we persist proof nodes (boundary nodes) via stitching
            Assert.IsFalse(db.KeyExists(rootHash));
        }

        [Test]
        public void RecreateAccountStateFromOneRangeWithExistenceProof()
        {
            Keccak rootHash = _inputTree.RootHash;   // "0x8c81279168edc449089449bc0f2136fc72c9645642845755633cf259cd97988b"

            byte[][] firstProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[0].Path.Bytes);
            byte[][] lastProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[5].Path.Bytes);

            MemDb db = new();
            DbProvider dbProvider = new(DbModeHint.Mem);
            dbProvider.RegisterDb(DbNames.State, db);
            ProgressTracker progressTracker = new(null, dbProvider.GetDb<IDb>(DbNames.State), LimboLogs.Instance);
            SnapProvider snapProvider = new(progressTracker, dbProvider, LimboLogs.Instance);
            var result = snapProvider.AddAccountRange(1, rootHash, TestItem.Tree.AccountsWithPaths[0].Path, TestItem.Tree.AccountsWithPaths, firstProof!.Concat(lastProof!).ToArray());

            Assert.AreEqual(AddRangeResult.OK, result);
            Assert.AreEqual(10, db.Keys.Count);  // we persist proof nodes (boundary nodes) via stitching
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

            Assert.AreEqual(AddRangeResult.OK, result);
            Assert.AreEqual(10, db.Keys.Count);  // we don't have the proofs so we persist all nodes
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

            byte[][] firstProof = CreateProofForPath(Keccak.Zero.Bytes);
            byte[][] lastProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[1].Path.Bytes);

            var result1 = snapProvider.AddAccountRange(1, rootHash, Keccak.Zero, TestItem.Tree.AccountsWithPaths[0..2], firstProof!.Concat(lastProof!).ToArray());

            Assert.AreEqual(2, db.Keys.Count);

            firstProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[2].Path.Bytes);
            lastProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[3].Path.Bytes);

            var result2 = snapProvider.AddAccountRange(1, rootHash, TestItem.Tree.AccountsWithPaths[2].Path, TestItem.Tree.AccountsWithPaths[2..4], firstProof!.Concat(lastProof!).ToArray());

            Assert.AreEqual(5, db.Keys.Count);  // we don't persist proof nodes (boundary nodes)

            firstProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[4].Path.Bytes);
            lastProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[5].Path.Bytes);

            var result3 = snapProvider.AddAccountRange(1, rootHash, TestItem.Tree.AccountsWithPaths[4].Path, TestItem.Tree.AccountsWithPaths[4..6], firstProof!.Concat(lastProof!).ToArray());

            Assert.AreEqual(AddRangeResult.OK, result1);
            Assert.AreEqual(AddRangeResult.OK, result2);
            Assert.AreEqual(AddRangeResult.OK, result3);
            Assert.AreEqual(10, db.Keys.Count);  // we persist proof nodes (boundary nodes) via stitching
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

            byte[][] firstProof = CreateProofForPath(Keccak.Zero.Bytes);
            byte[][] lastProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[1].Path.Bytes);

            var result1 = snapProvider.AddAccountRange(1, rootHash, Keccak.Zero, TestItem.Tree.AccountsWithPaths[0..2], firstProof!.Concat(lastProof!).ToArray());

            Assert.AreEqual(2, db.Keys.Count);

            firstProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[2].Path.Bytes);
            lastProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[3].Path.Bytes);

            // missing TestItem.Tree.AccountsWithHashes[2]
            var result2 = snapProvider.AddAccountRange(1, rootHash, TestItem.Tree.AccountsWithPaths[2].Path, TestItem.Tree.AccountsWithPaths[3..4], firstProof!.Concat(lastProof!).ToArray());

            Assert.AreEqual(2, db.Keys.Count);

            firstProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[4].Path.Bytes);
            lastProof = CreateProofForPath(TestItem.Tree.AccountsWithPaths[5].Path.Bytes);

            var result3 = snapProvider.AddAccountRange(1, rootHash, TestItem.Tree.AccountsWithPaths[4].Path, TestItem.Tree.AccountsWithPaths[4..6], firstProof!.Concat(lastProof!).ToArray());

            Assert.AreEqual(AddRangeResult.OK, result1);
            Assert.AreEqual(AddRangeResult.DifferentRootHash, result2);
            Assert.AreEqual(AddRangeResult.OK, result3);
            Assert.AreEqual(6, db.Keys.Count);
            Assert.IsFalse(db.KeyExists(rootHash));
        }
    }
}

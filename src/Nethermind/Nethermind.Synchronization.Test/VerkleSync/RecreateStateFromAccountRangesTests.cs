// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Verkle;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Synchronization.VerkleSync;
using Nethermind.Trie.Pruning;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Tree.Proofs;
using Nethermind.Verkle.Tree.Sync;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.VerkleSync
{
    [TestFixture]
    public class RecreateVerkleStateFromAccountRangesTests
    {
        private VerkleStateTree _inputTree;

        [OneTimeSetUp]
        public void Setup()
        {
            _inputTree = TestItem.Tree.GetVerkleStateTreeForSync(null);
        }

        [Test]
        public void RecreateAccountStateFromOneRangeWithNonExistenceProof()
        {
            VerkleProof proof =
                _inputTree.CreateVerkleRangeProof(Keccak.Zero.Bytes[..31].ToArray(), TestItem.Tree.stem5, out Banderwagon rootPoint);

            IDbProvider dbProvider = VerkleDbFactory.InitDatabase(DbMode.MemDb, null);
            VerkleProgressTracker progressTracker = new(null, dbProvider.GetDb<IDb>(DbNames.State), LimboLogs.Instance);
            VerkleSyncProvider verkleSyncProvider = new(progressTracker, dbProvider, LimboLogs.Instance);
            AddRangeResult result = verkleSyncProvider.AddSubTreeRange(1, rootPoint, Keccak.Zero.Bytes[..31].ToArray(), TestItem.Tree.SubTreesWithPaths, proof, TestItem.Tree.stem5);
            Assert.That(result, Is.EqualTo(AddRangeResult.OK));

            Console.WriteLine(dbProvider.InternalNodesDb.GetSize());
        }

        [Test]
        public void RecreateAccountStateFromOneRangeWithExistenceProof()
        {
            VerkleProof proof =
                _inputTree.CreateVerkleRangeProof(TestItem.Tree.stem0, TestItem.Tree.stem5, out Banderwagon rootPoint);


            IDbProvider dbProvider = VerkleDbFactory.InitDatabase(DbMode.MemDb, null);
            VerkleProgressTracker progressTracker = new(null, dbProvider.GetDb<IDb>(DbNames.State), LimboLogs.Instance);
            VerkleSyncProvider verkleSyncProvider = new(progressTracker, dbProvider, LimboLogs.Instance);
            AddRangeResult result = verkleSyncProvider.AddSubTreeRange(1, rootPoint, TestItem.Tree.stem0, TestItem.Tree.SubTreesWithPaths, proof, TestItem.Tree.stem5);
            Assert.That(result, Is.EqualTo(AddRangeResult.OK));
        }

        [Test]
        public void RecreateAccountStateFromMultipleRange()
        {
            IDbProvider dbProvider = VerkleDbFactory.InitDatabase(DbMode.MemDb, null);
            VerkleProgressTracker progressTracker = new(null, dbProvider.GetDb<IDb>(DbNames.State), LimboLogs.Instance);
            VerkleSyncProvider verkleSyncProvider = new(progressTracker, dbProvider, LimboLogs.Instance);

            PathWithSubTree[] pathWithSubTrees = TestItem.Tree.SubTreesWithPaths;

            VerkleProof proof1 =
                _inputTree.CreateVerkleRangeProof(TestItem.Tree.stem0, TestItem.Tree.stem1, out Banderwagon rootPoint);
            VerkleProof proof2 =
                _inputTree.CreateVerkleRangeProof(TestItem.Tree.stem2, TestItem.Tree.stem3, out rootPoint);
            VerkleProof proof3 =
                _inputTree.CreateVerkleRangeProof(TestItem.Tree.stem4, TestItem.Tree.stem5, out rootPoint);

            AddRangeResult result1 = verkleSyncProvider.AddSubTreeRange(1, rootPoint, TestItem.Tree.stem0, pathWithSubTrees[..2], proof1, TestItem.Tree.stem1);
            Assert.That(result1, Is.EqualTo(AddRangeResult.OK));

            AddRangeResult result2 = verkleSyncProvider.AddSubTreeRange(1, rootPoint, TestItem.Tree.stem2, pathWithSubTrees[2..4], proof2, TestItem.Tree.stem3);
            Assert.That(result2, Is.EqualTo(AddRangeResult.OK));

            AddRangeResult result3 = verkleSyncProvider.AddSubTreeRange(1, rootPoint, TestItem.Tree.stem4, pathWithSubTrees[4..], proof3, TestItem.Tree.stem5);
            Assert.That(result3, Is.EqualTo(AddRangeResult.OK));
        }

        [Test]
        public void RecreateAccountStateFromMultipleRange_InReverseOrder()
        {
            IDbProvider dbProvider = VerkleDbFactory.InitDatabase(DbMode.MemDb, null);
            VerkleProgressTracker progressTracker = new(null, dbProvider.GetDb<IDb>(DbNames.State), LimboLogs.Instance);
            VerkleSyncProvider verkleSyncProvider = new(progressTracker, dbProvider, LimboLogs.Instance);
            PathWithSubTree[] pathWithSubTrees = TestItem.Tree.SubTreesWithPaths;

            VerkleProof proof1 =
                _inputTree.CreateVerkleRangeProof(TestItem.Tree.stem0, TestItem.Tree.stem1, out Banderwagon rootPoint);
            VerkleProof proof2 =
                _inputTree.CreateVerkleRangeProof(TestItem.Tree.stem2, TestItem.Tree.stem3, out rootPoint);
            VerkleProof proof3 =
                _inputTree.CreateVerkleRangeProof(TestItem.Tree.stem4, TestItem.Tree.stem5, out rootPoint);

            AddRangeResult result1 = verkleSyncProvider.AddSubTreeRange(1, rootPoint, TestItem.Tree.stem4, pathWithSubTrees[4..], proof3, TestItem.Tree.stem5);
            Assert.That(result1, Is.EqualTo(AddRangeResult.OK));

            AddRangeResult result2 = verkleSyncProvider.AddSubTreeRange(1, rootPoint, TestItem.Tree.stem2, pathWithSubTrees[2..4], proof2, TestItem.Tree.stem3);
            Assert.That(result2, Is.EqualTo(AddRangeResult.OK));

            AddRangeResult result3 = verkleSyncProvider.AddSubTreeRange(1, rootPoint, TestItem.Tree.stem0, pathWithSubTrees[..2], proof1, TestItem.Tree.stem1);
            Assert.That(result3, Is.EqualTo(AddRangeResult.OK));
        }

        [Test]
        public void RecreateAccountStateFromMultipleRange_OutOfOrder()
        {
            IDbProvider dbProvider = VerkleDbFactory.InitDatabase(DbMode.MemDb, null);
            VerkleProgressTracker progressTracker = new(null, dbProvider.GetDb<IDb>(DbNames.State), LimboLogs.Instance);
            VerkleSyncProvider verkleSyncProvider = new(progressTracker, dbProvider, LimboLogs.Instance);
            PathWithSubTree[] pathWithSubTrees = TestItem.Tree.SubTreesWithPaths;

            VerkleProof proof1 =
                _inputTree.CreateVerkleRangeProof(TestItem.Tree.stem0, TestItem.Tree.stem1, out Banderwagon rootPoint);
            VerkleProof proof2 =
                _inputTree.CreateVerkleRangeProof(TestItem.Tree.stem2, TestItem.Tree.stem3, out rootPoint);
            VerkleProof proof3 =
                _inputTree.CreateVerkleRangeProof(TestItem.Tree.stem4, TestItem.Tree.stem5, out rootPoint);

            AddRangeResult result2 = verkleSyncProvider.AddSubTreeRange(1, rootPoint, TestItem.Tree.stem2, pathWithSubTrees[2..4], proof2, TestItem.Tree.stem3);
            Assert.That(result2, Is.EqualTo(AddRangeResult.OK));

            AddRangeResult result1 = verkleSyncProvider.AddSubTreeRange(1, rootPoint, TestItem.Tree.stem0, pathWithSubTrees[..2], proof1, TestItem.Tree.stem1);
            Assert.That(result1, Is.EqualTo(AddRangeResult.OK));

            AddRangeResult result3 = verkleSyncProvider.AddSubTreeRange(1, rootPoint, TestItem.Tree.stem4, pathWithSubTrees[4..], proof3, TestItem.Tree.stem5);
            Assert.That(result3, Is.EqualTo(AddRangeResult.OK));
        }

        [Test]
        public void RecreateAccountStateFromMultipleOverlappingRange()
        {
            IDbProvider dbProvider = VerkleDbFactory.InitDatabase(DbMode.MemDb, null);
            VerkleProgressTracker progressTracker = new(null, dbProvider.GetDb<IDb>(DbNames.State), LimboLogs.Instance);
            VerkleSyncProvider verkleSyncProvider = new(progressTracker, dbProvider, LimboLogs.Instance);

            PathWithSubTree[] pathWithSubTrees = TestItem.Tree.SubTreesWithPaths;

            VerkleProof proof1 =
                _inputTree.CreateVerkleRangeProof(TestItem.Tree.stem0, TestItem.Tree.stem2, out Banderwagon rootPoint);
            VerkleProof proof2 =
                _inputTree.CreateVerkleRangeProof(TestItem.Tree.stem2, TestItem.Tree.stem3, out rootPoint);
            VerkleProof proof3 =
                _inputTree.CreateVerkleRangeProof(TestItem.Tree.stem3, TestItem.Tree.stem4, out rootPoint);
            VerkleProof proof4 =
                _inputTree.CreateVerkleRangeProof(TestItem.Tree.stem4, TestItem.Tree.stem5, out rootPoint);

            AddRangeResult result2 = verkleSyncProvider.AddSubTreeRange(1, rootPoint, TestItem.Tree.stem0, pathWithSubTrees[..3], proof1, TestItem.Tree.stem2);
            Assert.That(result2, Is.EqualTo(AddRangeResult.OK));

            AddRangeResult result1 = verkleSyncProvider.AddSubTreeRange(1, rootPoint, TestItem.Tree.stem2, pathWithSubTrees[2..4], proof2, TestItem.Tree.stem3);
            Assert.That(result1, Is.EqualTo(AddRangeResult.OK));

            AddRangeResult result3 = verkleSyncProvider.AddSubTreeRange(1, rootPoint, TestItem.Tree.stem3, pathWithSubTrees[3..5], proof3, TestItem.Tree.stem4);
            Assert.That(result3, Is.EqualTo(AddRangeResult.OK));

            AddRangeResult result4 = verkleSyncProvider.AddSubTreeRange(1, rootPoint, TestItem.Tree.stem4, pathWithSubTrees[4..6], proof4, TestItem.Tree.stem5);
            Assert.That(result4, Is.EqualTo(AddRangeResult.OK));
        }
    }
}

// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Verkle;
using Nethermind.Db.Rocks;
using Nethermind.Logging;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Tree.Sync;
using Nethermind.Verkle.Tree.TreeStore;

namespace Nethermind.Verkle.Tree.Test;

public class VerkleRangeProofTests
{
    [Test]
    public void TestSyncProofCreationAndVerification()
    {
        VerkleTree tree = VerkleTestUtils.GetVerkleTreeForTest<VerkleSyncCache>(DbMode.MemDb);

        byte[][] stems =
        [
            [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2],
            [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1],
            [0, 0, 0, 0, 0, 0, 0, 0, 0, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 3],
            [0, 0, 0, 0, 0, 0, 0, 0, 0, 5, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 4]
        ];

        byte[][] values =
        [
            VerkleTestUtils.KeyVersion.BytesToArray(),
            VerkleTestUtils.KeyBalance.BytesToArray(),
            VerkleTestUtils.KeyNonce.BytesToArray(),
            VerkleTestUtils.KeyCodeCommitment.BytesToArray(),
            VerkleTestUtils.KeyCodeSize.BytesToArray()
        ];

        foreach (byte[] stem in stems)
        {
            List<(byte, byte[])> batch = [];
            for (byte i = 0; i < 5; i++) batch.Add((i, values[0]));
            tree.InsertStemBatch(stem, batch);
        }
        tree.Commit();
        tree.CommitTree(0);

        VerkleProof proof = tree.CreateVerkleRangeProof(stems[0], stems[^1], out Banderwagon root);



        var subTrees = new List<PathWithSubTree>();
        var leafs = new List<LeafInSubTree>();

        for (byte i = 0; i < 5; i++) leafs.Add((i, values[0]));
        subTrees.Add(new PathWithSubTree(stems[0], leafs.ToArray()));
        subTrees.Add(new PathWithSubTree(stems[1], leafs.ToArray()));
        subTrees.Add(new PathWithSubTree(stems[2], leafs.ToArray()));
        subTrees.Add(new PathWithSubTree(stems[3], leafs.ToArray()));

        IVerkleTreeStore? newStore = VerkleTestUtils.GetVerkleStoreForTest<PersistEveryBlock>(DbMode.MemDb);
        var newTree = new VerkleTree(newStore, LimboLogs.Instance);
        bool isTrue =
            newTree.CreateStatelessTreeFromRange(proof, root, stems[0], stems[^1], subTrees.ToArray());
        Assert.That(isTrue, Is.True);

        newStore.InsertSyncBatch(0, newTree._treeCache);
        newStore.InsertRootNodeAfterSyncCompletion(tree.StateRoot.BytesToArray(), 0);
        VerkleTreeDumper oldTreeDumper = new();
        VerkleTreeDumper newTreeDumper = new();

        tree.Accept(oldTreeDumper, new Hash256(root.ToBytes()));
        newTree.Accept(newTreeDumper, new Hash256(root.ToBytes()));

        oldTreeDumper.ToString().Should().BeEquivalentTo(newTreeDumper.ToString());
    }

    [Test]
    public void TestSyncProofCreationAndVerificationEdgeCase()
    {
        VerkleTree tree = VerkleTestUtils.GetVerkleTreeForTest<VerkleSyncCache>(DbMode.MemDb);

        byte[] stem = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2];

        byte[][] values =
        [
            VerkleTestUtils.KeyVersion.BytesToArray(),
            VerkleTestUtils.KeyBalance.BytesToArray(),
            VerkleTestUtils.KeyNonce.BytesToArray(),
            VerkleTestUtils.KeyCodeCommitment.BytesToArray(),
            VerkleTestUtils.KeyCodeSize.BytesToArray()
        ];


        List<(byte, byte[])> batch = [];
        for (byte i = 0; i < 5; i++) batch.Add((i, values[0]));
        tree.InsertStemBatch(stem, batch);
        tree.Commit();
        tree.CommitTree(0);

        VerkleProof proof = tree.CreateVerkleRangeProof(stem, stem, out Banderwagon root);

        List<PathWithSubTree> subTrees = [];
        List<LeafInSubTree> leafs = [];

        for (byte i = 0; i < 5; i++) leafs.Add((i, values[0]));
        subTrees.Add(new PathWithSubTree(stem, leafs.ToArray()));


        IVerkleTreeStore? newStore = VerkleTestUtils.GetVerkleStoreForTest<PersistEveryBlock>(DbMode.MemDb);
        var newTree = new VerkleTree(newStore, LimboLogs.Instance);
        bool isTrue =
            newTree.CreateStatelessTreeFromRange(proof, root, subTrees[0].Path, subTrees[^1].Path, subTrees.ToArray());
        Assert.That(isTrue, Is.True);

        newStore.InsertSyncBatch(0, newTree._treeCache);
        newStore.InsertRootNodeAfterSyncCompletion(tree.StateRoot.BytesToArray(), 0);
        VerkleTreeDumper oldTreeDumper = new();
        VerkleTreeDumper newTreeDumper = new();

        tree.Accept(oldTreeDumper, new Hash256(root.ToBytes()));
        newTree.Accept(newTreeDumper, new Hash256(root.ToBytes()));
        oldTreeDumper.ToString().Should().BeEquivalentTo(newTreeDumper.ToString());
    }

    [Test]
    public void TestSyncProofCreationAndVerificationLastBytesDiffering()
    {
        VerkleTree tree = VerkleTestUtils.GetVerkleTreeForTest<VerkleSyncCache>(DbMode.MemDb);

        byte[][] stems =
        [
            [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1],
            [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2],
            [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 3],
            [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 4]
        ];

        byte[][] values =
        [
            VerkleTestUtils.KeyVersion.BytesToArray(),
            VerkleTestUtils.KeyBalance.BytesToArray(),
            VerkleTestUtils.KeyNonce.BytesToArray(),
            VerkleTestUtils.KeyCodeCommitment.BytesToArray(),
            VerkleTestUtils.KeyCodeSize.BytesToArray()
        ];

        foreach (byte[] stem in stems)
        {
            List<(byte, byte[])> batch = [];
            for (byte i = 0; i < 5; i++) batch.Add((i, values[i]));
            tree.InsertStemBatch(stem, batch);
        }
        tree.Commit();
        tree.CommitTree(0);

        VerkleProof proof = tree.CreateVerkleRangeProof(stems[0], stems[^1], out Banderwagon root);

        // bool verified = VerkleTree.VerifyVerkleRangeProof(proof, stems[0], stems[^1], stems, root, out _);
        // Assert.That(verified, Is.True);

        List<PathWithSubTree> subTrees = [];
        List<LeafInSubTree> leafs = [];

        for (byte i = 0; i < 5; i++) leafs.Add((i, values[i]));
        subTrees.Add(new PathWithSubTree(stems[0], leafs.ToArray()));
        subTrees.Add(new PathWithSubTree(stems[1], leafs.ToArray()));
        subTrees.Add(new PathWithSubTree(stems[2], leafs.ToArray()));
        subTrees.Add(new PathWithSubTree(stems[3], leafs.ToArray()));


        IVerkleTreeStore? newStore = VerkleTestUtils.GetVerkleStoreForTest<PersistEveryBlock>(DbMode.MemDb);
        var newTree = new VerkleTree(newStore, LimboLogs.Instance);
        bool isTrue =
            newTree.CreateStatelessTreeFromRange(proof, root, stems[0], stems[^1], subTrees.ToArray());
        Assert.That(isTrue, Is.True);

        newStore.InsertSyncBatch(0, newTree._treeCache);
        newStore.InsertRootNodeAfterSyncCompletion(tree.StateRoot.BytesToArray(), 0);

        VerkleTreeDumper oldTreeDumper = new();
        VerkleTreeDumper newTreeDumper = new();

        tree.Accept(oldTreeDumper, new Hash256(root.ToBytes()));
        newTree.Accept(newTreeDumper, new Hash256(root.ToBytes()));

        Console.WriteLine("oldTreeDumper");
        Console.WriteLine(oldTreeDumper.ToString());
        Console.WriteLine("newTreeDumper");
        Console.WriteLine(newTreeDumper.ToString());
    }
}

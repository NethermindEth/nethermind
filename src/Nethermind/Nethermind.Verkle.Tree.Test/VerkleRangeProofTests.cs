// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Verkle;
using Nethermind.Db.Rocks;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Tree.Proofs;
using Nethermind.Verkle.Tree.Sync;
using Nethermind.Verkle.Tree.Utils;

namespace Nethermind.Verkle.Tree.Test;

public class VerkleRangeProofTests
{
    [Test]
    public void TestSyncProofCreationAndVerification()
    {
        VerkleTree tree = VerkleTestUtils.GetVerkleTreeForTest(DbMode.MemDb);

        byte[][] stems =
        {
            new byte[]{0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2},
            new byte[]{0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1},
            new byte[]{0, 0, 0, 0, 0, 0, 0, 0, 0, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 3},
            new byte[]{0, 0, 0, 0, 0, 0, 0, 0, 0, 5, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 4}
        };

        byte[][] values =
        {
            VerkleTestUtils._keyVersion, VerkleTestUtils._keyBalance, VerkleTestUtils._keyNonce,
            VerkleTestUtils._keyCodeCommitment, VerkleTestUtils._keyCodeSize,
        };

        foreach (byte[] stem in stems)
        {
            List<(byte, byte[])> batch = new();
            for (byte i = 0; i < 5; i++) batch.Add((i, values[0]));
            tree.InsertStemBatch(stem, batch);
        }
        tree.Commit();
        tree.CommitTree(0);

        VerkleProof proof = tree.CreateVerkleRangeProof(stems[0], stems[^1], out Banderwagon root);

        // bool verified = VerkleTree.VerifyVerkleRangeProof(proof, stems[0], stems[^1], stems, root, out _);
        // Assert.That(verified, Is.True);

        VerkleTree newTree = VerkleTestUtils.GetVerkleTreeForTest(DbMode.MemDb);

        List<PathWithSubTree> subTrees = new List<PathWithSubTree>();
        List<LeafInSubTree> leafs = new List<LeafInSubTree>();

        for (byte i = 0; i < 5; i++) leafs.Add((i, values[0]));
        subTrees.Add(new PathWithSubTree(stems[0], leafs.ToArray()));
        subTrees.Add(new PathWithSubTree(stems[1], leafs.ToArray()));
        subTrees.Add(new PathWithSubTree(stems[2], leafs.ToArray()));
        subTrees.Add(new PathWithSubTree(stems[3], leafs.ToArray()));


        bool isTrue =
            newTree.CreateStatelessTreeFromRange(proof, root, stems[0], stems[^1], subTrees.ToArray());
        Assert.That(isTrue, Is.True);

        VerkleTreeDumper oldTreeDumper = new();
        VerkleTreeDumper newTreeDumper = new();

        tree.Accept(oldTreeDumper, new VerkleCommitment(root.ToBytes()));
        newTree.Accept(newTreeDumper, new VerkleCommitment(root.ToBytes()));

        Console.WriteLine("oldTreeDumper");
        Console.WriteLine(oldTreeDumper.ToString());
        Console.WriteLine("newTreeDumper");
        Console.WriteLine(newTreeDumper.ToString());
    }

    [Test]
    public void TestSyncProofCreationAndVerificationLastBytesDiffering()
    {
        VerkleTree tree = VerkleTestUtils.GetVerkleTreeForTest(DbMode.MemDb);

        byte[][] stems =
        {
            new byte[]{0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1},
            new byte[]{0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2},
            new byte[]{0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 3},
            new byte[]{0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 4}
        };

        byte[][] values =
        {
            VerkleTestUtils._keyVersion, VerkleTestUtils._keyBalance, VerkleTestUtils._keyNonce,
            VerkleTestUtils._keyCodeCommitment, VerkleTestUtils._keyCodeSize,
        };

        foreach (byte[] stem in stems)
        {
            List<(byte, byte[])> batch = new();
            for (byte i = 0; i < 5; i++) batch.Add((i, values[i]));
            tree.InsertStemBatch(stem, batch);
        }
        tree.Commit();
        tree.CommitTree(0);

        VerkleProof proof = tree.CreateVerkleRangeProof(stems[0], stems[^1], out Banderwagon root);

        // bool verified = VerkleTree.VerifyVerkleRangeProof(proof, stems[0], stems[^1], stems, root, out _);
        // Assert.That(verified, Is.True);

        VerkleTree newTree = VerkleTestUtils.GetVerkleTreeForTest(DbMode.MemDb);

        List<PathWithSubTree> subTrees = new List<PathWithSubTree>();
        List<LeafInSubTree> leafs = new List<LeafInSubTree>();

        for (byte i = 0; i < 5; i++) leafs.Add((i, values[i]));
        subTrees.Add(new PathWithSubTree(stems[0], leafs.ToArray()));
        subTrees.Add(new PathWithSubTree(stems[1], leafs.ToArray()));
        subTrees.Add(new PathWithSubTree(stems[2], leafs.ToArray()));
        subTrees.Add(new PathWithSubTree(stems[3], leafs.ToArray()));


        bool isTrue =
            newTree.CreateStatelessTreeFromRange(proof, root, stems[0], stems[^1], subTrees.ToArray());
        Assert.That(isTrue, Is.True);

        VerkleTreeDumper oldTreeDumper = new();
        VerkleTreeDumper newTreeDumper = new();

        tree.Accept(oldTreeDumper, new VerkleCommitment(root.ToBytes()));
        newTree.Accept(newTreeDumper, new VerkleCommitment(root.ToBytes()));

        Console.WriteLine("oldTreeDumper");
        Console.WriteLine(oldTreeDumper.ToString());
        Console.WriteLine("newTreeDumper");
        Console.WriteLine(newTreeDumper.ToString());
    }
}

// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Verkle;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Tree.Nodes;
using Nethermind.Verkle.Tree.Serializers;

namespace Nethermind.Verkle.Tree.Test;

[TestFixture]
public class NodeSerializerTests
{

    private readonly Random _rand = new(0);

    [Test]
    public void TestBranchNode()
    {
        byte[] commitment = new byte[32];
        _rand.NextBytes(commitment);
        _rand.NextBytes(commitment);
        InternalNode node = new (VerkleNodeType.BranchNode, new Commitment(Banderwagon.FromBytes(commitment)!.Value));
        AssertEncodeDecodeEqual(node);
    }

    [Test]
    public void TestFullStemNode()
    {
        byte[] commitment = new byte[32];
        byte[] c1 = new byte[32];
        byte[] c2 = new byte[32];
        byte[] stem = new byte[31];

        Banderwagon? c1Point;
        Banderwagon? c2Point;
        Banderwagon? internalCommitment;
        while (true)
        {
            _rand.NextBytes(commitment);
            internalCommitment = Banderwagon.FromBytes(commitment);
            if (internalCommitment.HasValue) break;
        }
        while (true)
        {
            _rand.NextBytes(c1);
            c1Point = Banderwagon.FromBytes(c1);
            if (c1Point.HasValue) break;
        }
        while (true)
        {
            _rand.NextBytes(c2);
            c2Point = Banderwagon.FromBytes(c2);
            if (c2Point.HasValue) break;
        }
        _rand.NextBytes(stem);

        InternalNode node = new(VerkleNodeType.StemNode, stem, new Commitment(c1Point.Value),
            new Commitment(c2Point.Value), new Commitment(internalCommitment.Value));
        AssertEncodeDecodeEqual(node);
    }

    [Test]
    public void TestC1NullStemNode()
    {
        byte[] commitment = new byte[32];
        byte[] c2 = new byte[32];
        byte[] stem = new byte[31];


        Banderwagon? c2Point;
        Banderwagon? internalCommitment;
        while (true)
        {
            _rand.NextBytes(commitment);
            internalCommitment = Banderwagon.FromBytes(commitment);
            if (internalCommitment.HasValue) break;
        }

        while (true)
        {
            _rand.NextBytes(c2);
            c2Point = Banderwagon.FromBytes(c2);
            if (c2Point.HasValue) break;
        }
        _rand.NextBytes(stem);

        InternalNode node = new(VerkleNodeType.StemNode, stem, null,
            new Commitment(c2Point.Value), new Commitment(internalCommitment.Value));
        AssertEncodeDecodeEqual(node);
    }

    [Test]
    public void TestC2NullStemNode()
    {
        byte[] commitment = new byte[32];
        byte[] c1 = new byte[32];
        byte[] stem = new byte[31];

        Banderwagon? c1Point;
        Banderwagon? internalCommitment;
        while (true)
        {
            _rand.NextBytes(commitment);
            internalCommitment = Banderwagon.FromBytes(commitment);
            if (internalCommitment.HasValue) break;
        }
        while (true)
        {
            _rand.NextBytes(c1);
            c1Point = Banderwagon.FromBytes(c1);
            if (c1Point.HasValue) break;
        }
        _rand.NextBytes(stem);

        InternalNode node = new(VerkleNodeType.StemNode, stem, new Commitment(c1Point.Value),
            null, new Commitment(internalCommitment.Value));
        AssertEncodeDecodeEqual(node);
    }

    [Test]
    public void TestNullNullStemNode()
    {
        byte[] commitment = new byte[32];
        byte[] stem = new byte[31];

        Banderwagon? internalCommitment;
        while (true)
        {
            _rand.NextBytes(commitment);
            internalCommitment = Banderwagon.FromBytes(commitment);
            if (internalCommitment.HasValue) break;
        }
        _rand.NextBytes(stem);

        InternalNode node = new(VerkleNodeType.StemNode, stem, null,
            null, new Commitment(internalCommitment.Value));
        AssertEncodeDecodeEqual(node);
    }

    [Test]
    public void TestZeroBanderwagonSerialized()
    {
        Banderwagon zero = new Banderwagon();
        var ser = zero.ToBytes();
        Banderwagon zero2 = Banderwagon.FromBytes(ser).Value;
        Assert.IsTrue(zero == zero2);
    }

    private void AssertEncodeDecodeEqual(InternalNode node1)
    {
        byte[] serNode = InternalNodeSerializer.Instance.Encode(node1).Bytes;
        InternalNode node2 = InternalNodeSerializer.Instance.Decode(serNode);
        Assert.That(node1.NodeType == node2.NodeType, Is.True);
        switch (node2.NodeType)
        {
            case VerkleNodeType.BranchNode:
                Assert.That(node1.InternalCommitment.Point == node2.InternalCommitment.Point, Is.True);
                break;
            case VerkleNodeType.StemNode:
                Assert.That(node1.Stem == node2.Stem, Is.True);
                Assert.That(node1.InternalCommitment.Point == node2.InternalCommitment.Point, Is.True);
                Assert.That(node1.C1?.Point == node2.C1?.Point, Is.True);
                Assert.That(node1.C2?.Point == node2.C2?.Point, Is.True);
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }
}

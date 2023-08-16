// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Text;
using Nethermind.Core.Extensions;
using Nethermind.Core.Verkle;
using Nethermind.Logging;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Fields.FrEElement;
using Nethermind.Verkle.Tree.Utils;

namespace Nethermind.Verkle.Tree.Nodes;

public class InternalNode
{
    public bool IsStem => NodeType == VerkleNodeType.StemNode;
    public bool IsBranchNode => NodeType == VerkleNodeType.BranchNode;
    public VerkleNodeType NodeType { get; }
    public Commitment InternalCommitment { get; }
    public byte[] Bytes => InternalCommitment.ToBytes();

    public bool IsStateless { get; set; } = false;

    public bool ShouldPersist
    {
        get
        {
            if (IsBranchNode) return true;
            return C1 is not null && C2 is not null && Stem is not null;
        }
    }


    /// <summary>
    ///  C1, C2, InitCommitmentHash - only relevant for stem nodes
    /// </summary>
    public Commitment? C1 { get; set; }
    public Commitment? C2 { get; set; }
    public FrE? InitCommitmentHash { get; }

    private static readonly Banderwagon _initFirstElementCommitment = Committer.ScalarMul(FrE.One, 0);

    public Stem? Stem { get; }

    public InternalNode Clone()
    {
        return NodeType switch
        {
            VerkleNodeType.BranchNode => new InternalNode(VerkleNodeType.BranchNode, InternalCommitment.Dup()),
            VerkleNodeType.StemNode => new InternalNode(VerkleNodeType.StemNode, Stem!, C1?.Dup(), C2?.Dup(), InternalCommitment.Dup()),
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    public InternalNode(VerkleNodeType nodeType)
    {
        NodeType = nodeType;
        InternalCommitment = new Commitment();
    }

    public InternalNode(VerkleNodeType nodeType, Commitment commitment)
    {
        NodeType = nodeType;
        InternalCommitment = commitment;
    }

    public InternalNode(VerkleNodeType nodeType, Stem stem)
    {
        NodeType = nodeType;
        Stem = stem;
        C1 = new Commitment();
        C2 = new Commitment();
        InternalCommitment = new Commitment();
        Banderwagon stemCommitment = GetInitialCommitment();
        InternalCommitment.AddPoint(stemCommitment);
        InitCommitmentHash = InternalCommitment.PointAsField;
    }

    public InternalNode(VerkleNodeType nodeType, Stem stem, Commitment? c1, Commitment? c2, Commitment internalCommitment)
    {
        NodeType = nodeType;
        Stem = stem;
        C1 = c1;
        C2 = c2;
        InternalCommitment = internalCommitment;
    }

    public InternalNode(VerkleNodeType nodeType, Stem stem, byte[] c1, byte[] c2, byte[] extCommit, bool subGroupCheck=false)
    {
        NodeType = nodeType;
        Stem = stem;
        C1 = new Commitment(Banderwagon.FromBytes(c1, subGroupCheck)!.Value);
        C2 = new Commitment(Banderwagon.FromBytes(c2, subGroupCheck)!.Value);
        InternalCommitment = new Commitment(Banderwagon.FromBytes(extCommit, subGroupCheck)!.Value);
    }

    private Banderwagon GetInitialCommitment()
    {
        return _initFirstElementCommitment +
               Committer.ScalarMul(FrE.FromBytesReduced(Stem!.Bytes.Reverse().ToArray()), 1);
    }

    public FrE UpdateCommitment(Banderwagon point)
    {
        Debug.Assert(NodeType == VerkleNodeType.BranchNode);
        FrE prevCommit = InternalCommitment.PointAsField;
        InternalCommitment.AddPoint(point);
        return InternalCommitment.PointAsField - prevCommit;
    }

    public FrE UpdateCommitment(LeafUpdateDelta deltaLeafCommitment)
    {
        Debug.Assert(NodeType == VerkleNodeType.StemNode);
        FrE deltaC1Commit = FrE.Zero;
        FrE deltaC2Commit = FrE.Zero;

        if (deltaLeafCommitment.DeltaC1 is not null)
        {
            FrE oldC1Value = C1!.PointAsField;
            C1.AddPoint(deltaLeafCommitment.DeltaC1.Value);
            deltaC1Commit = C1.PointAsField - oldC1Value;
        }
        if (deltaLeafCommitment.DeltaC2 is not null)
        {
            FrE oldC2Value = C2!.PointAsField;
            C2.AddPoint(deltaLeafCommitment.DeltaC2.Value);
            deltaC2Commit = C2.PointAsField - oldC2Value;
        }

        Banderwagon deltaCommit = Committer.ScalarMul(deltaC1Commit, 2)
                                  + Committer.ScalarMul(deltaC2Commit, 3);

        return InternalCommitment.UpdateCommitmentGetDelta(deltaCommit);
    }

    public override string ToString()
    {
        StringBuilder builder = new StringBuilder();
        builder.AppendLine($"InternalNode: {InternalCommitment.Point.ToBytes().ToHexString()}");
        builder.AppendLine($"NodeType: {NodeType}");
        if (NodeType == VerkleNodeType.StemNode)
        {
            builder.AppendLine($"Stem: {Stem.ToString()}");
            builder.AppendLine($"C1: {C1?.Point.ToBytes().ToHexString()}");
            builder.AppendLine($"C2: {C2?.Point.ToBytes().ToHexString()}");
        }

        return builder.ToString();
    }
}

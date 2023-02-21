// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Fields.FrEElement;
using Nethermind.Verkle.Utils;

namespace Nethermind.Verkle.Tree.Nodes;

public class StemNode : InternalNode
{
    public StemNode(byte[] stem, Commitment suffixCommitment) : base(NodeType.StemNode, stem, suffixCommitment)
    {
    }
}

public class BranchNode : InternalNode
{
    public BranchNode() : base(NodeType.BranchNode)
    {
    }
}

public class InternalNode
{
    public bool IsStem => NodeType == NodeType.StemNode;
    public bool IsBranchNode => NodeType == NodeType.BranchNode;

    public readonly Commitment _internalCommitment;

    public readonly NodeType NodeType;

    private byte[]? _stem;
    public byte[] Stem
    {
        get
        {
            Debug.Assert(_stem != null, nameof(_stem) + " != null");
            return _stem;
        }
    }

    public InternalNode(NodeType nodeType, byte[] stem, Commitment suffixCommitment)
    {
        switch (nodeType)
        {
            case NodeType.StemNode:
                NodeType = NodeType.StemNode;
                _stem = stem;
                _internalCommitment = suffixCommitment;
                break;
            case NodeType.BranchNode:
            default:
                throw new ArgumentOutOfRangeException(nameof(nodeType), nodeType, null);
        }
    }

    public InternalNode(NodeType nodeType)
    {
        switch (nodeType)
        {
            case NodeType.BranchNode:
                break;
            case NodeType.StemNode:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(nodeType), nodeType, null);
        }
        NodeType = nodeType;
        _internalCommitment = new Commitment();
    }
    public FrE UpdateCommitment(Banderwagon point)
    {
        FrE prevCommit = _internalCommitment.PointAsField.Dup();
        _internalCommitment.AddPoint(point);
        return _internalCommitment.PointAsField - prevCommit;
    }

    public byte[] Encode()
    {
        int nodeLength;
        byte[] rlp;
        switch (NodeType)
        {
            case NodeType.BranchNode:
                nodeLength = 32 + 1;
                rlp = new byte[nodeLength];
                rlp[0] = (byte)NodeType;
                Buffer.BlockCopy(_internalCommitment.Point.ToBytes(), 0, rlp, 1, 32);
                return rlp;
            case NodeType.StemNode:
                nodeLength = 32 + 31 + 1;
                rlp = new byte[nodeLength];
                rlp[0] = (byte)NodeType;
                Buffer.BlockCopy(_stem, 0, rlp, 1, 32);
                Buffer.BlockCopy(_internalCommitment.Point.ToBytes(), 0, rlp, 32, 32);
                return rlp;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    public static InternalNode Decode(byte[] rlp)
    {
        NodeType nodeType = (NodeType)rlp[0];
        switch (nodeType)
        {
            case NodeType.BranchNode:
                InternalNode node = new InternalNode(nodeType);
                node.UpdateCommitment(new Banderwagon(rlp[1..]));
                return node;
            case NodeType.StemNode:
                return new InternalNode(NodeType.StemNode, rlp[1..32], new Commitment(new Banderwagon(rlp[32..])));
            default:
                throw new ArgumentOutOfRangeException();
        }
    }
}

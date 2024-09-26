// Copyright 2022 Demerzel Solutions Limited
// Licensed under Apache-2.0.For full terms, see LICENSE in the project root.

using System.Text;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Fields.FrEElement;
using Nethermind.Verkle.Polynomial;

namespace Nethermind.Verkle.Proofs;

public readonly ref struct IpaProverQuery(
    Span<FrE> polynomial,
    Banderwagon commitment,
    FrE point,
    Span<FrE> pointEvaluations)
{
    public readonly Banderwagon Commitment = commitment;
    public readonly FrE Point = point;
    public readonly Span<FrE> PointEvaluations = pointEvaluations;
    public readonly Span<FrE> Polynomial = polynomial;
}

public class IpaVerifierQuery(
    Banderwagon commitment,
    FrE point,
    FrE[] pointEvaluations,
    FrE outputPoint,
    IpaProofStruct ipaProof)
{
    public readonly Banderwagon Commitment = commitment;
    public readonly IpaProofStruct IpaProof = ipaProof;
    public readonly FrE OutputPoint = outputPoint;
    public readonly FrE Point = point;
    public readonly FrE[] PointEvaluations = pointEvaluations;
}

public struct VerkleProverQuery
{
    public readonly LagrangeBasis ChildHashPoly;
    public readonly Banderwagon NodeCommitPoint;
    public readonly byte ChildIndex;
    public readonly FrE ChildHash;

    public VerkleProverQuery(LagrangeBasis childHashPoly, Banderwagon nodeCommitPoint, byte childIndex,
        FrE childHash)
    {
        ChildHashPoly = childHashPoly;
        NodeCommitPoint = nodeCommitPoint;
        ChildIndex = childIndex;
        ChildHash = childHash;
    }
}

public readonly struct VerkleProverQuerySerialized(byte[][] childHashPoly, byte[] nodeCommitPoint, byte childIndex, byte[] childHash)
{
    public readonly byte[][] ChildHashPoly = childHashPoly;
    public readonly byte[] NodeCommitPoint = nodeCommitPoint;
    public readonly byte ChildIndex = childIndex;
    public readonly byte[] ChildHash = childHash;

    public static VerkleProverQuerySerialized CreateProverQuerySerialized(VerkleProverQuery query)
    {
        byte[] nodeCommitPoint = query.NodeCommitPoint.ToBytesUncompressedLittleEndian();
        byte[][] childHashPoly = new byte[256][];
        int i = 0;
        foreach (FrE eval in query.ChildHashPoly.Evaluations)
        {
            childHashPoly[i] = eval.ToBytes();
            i++;
        }
        byte childIndex = query.ChildIndex;
        byte[] childHash = query.ChildHash.ToBytes();

        return new VerkleProverQuerySerialized(childHashPoly, nodeCommitPoint, childIndex, childHash);
    }

    public byte[] Encode()
    {
        byte[] encoded = new byte[8289];
        Span<byte> span = encoded;

        NodeCommitPoint.CopyTo(span.Slice(0, 64));
        int offset = 64;

        foreach (byte[] eval in ChildHashPoly)
        {
            eval.CopyTo(span.Slice(offset, 32));
            offset += 32;
        }

        span[8256] = ChildIndex;
        ChildHash.CopyTo(span.Slice(8257, 32));

        return encoded;
    }

    public override string ToString()
    {
        StringBuilder stringBuilder = new();
        stringBuilder.Append("\n#[_ChildHashPoly]#\n");
        foreach (byte[] eval in ChildHashPoly)
        {
            stringBuilder.AppendJoin(", ", eval);
            stringBuilder.Append('\n');
        }
        stringBuilder.Append("\n#[_NodeCommitPoint]#\n");
        stringBuilder.AppendJoin(", ", NodeCommitPoint);
        stringBuilder.Append("\n#[_ChildIndex]#\n");
        stringBuilder.AppendJoin(", ", ChildIndex);
        stringBuilder.Append("\n#[_ChildHash]#\n");
        stringBuilder.AppendJoin(", ", ChildHash);
        return stringBuilder.ToString();
    }
}

public struct VerkleVerifierQuery
{
    public readonly Banderwagon NodeCommitPoint;
    public readonly byte ChildIndex;
    public readonly FrE ChildHash;

    public VerkleVerifierQuery(Banderwagon nodeCommitPoint, byte childIndex, FrE childHash)
    {
        NodeCommitPoint = nodeCommitPoint;
        ChildIndex = childIndex;
        ChildHash = childHash;
    }
}

public readonly struct VerkleVerifierQuerySerialized(byte[] NodeCommitPoint, byte ChildIndex, byte[] ChildHash)
{
    public readonly byte[] NodeCommitPoint = NodeCommitPoint;
    public readonly byte ChildIndex = ChildIndex;
    public readonly byte[] ChildHash = ChildHash;

    public byte[] Encode()
    {
        byte[] encoded = new byte[97];
        Span<byte> span = encoded;

        NodeCommitPoint.CopyTo(span.Slice(0, 64));
        span[64] = ChildIndex;
        ChildHash.CopyTo(span.Slice(65, 32));

        return encoded;
    }

    public override string ToString()
    {
        StringBuilder stringBuilder = new();
        stringBuilder.Append("\n#[_NodeCommitPoint]#\n");
        stringBuilder.AppendJoin(", ", NodeCommitPoint);
        stringBuilder.Append("\n#[_ChildIndex]#\n");
        stringBuilder.AppendJoin(", ", ChildIndex);
        stringBuilder.Append("\n#[_ChildHash]#\n");
        stringBuilder.AppendJoin(", ", ChildHash);
        return stringBuilder.ToString();
    }
}
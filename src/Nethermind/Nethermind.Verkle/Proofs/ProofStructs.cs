// Copyright 2022 Demerzel Solutions Limited
// Licensed under Apache-2.0.For full terms, see LICENSE in the project root.

using System.Text;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Fields.FrEElement;

namespace Nethermind.Verkle.Proofs;

public readonly struct IpaProofStruct(Banderwagon[] l, FrE a, Banderwagon[] r)
{
    public readonly FrE A = a;
    public readonly Banderwagon[] L = l;
    public readonly Banderwagon[] R = r;

    public byte[] Encode()
    {
        List<byte> encoded = [];

        foreach (Banderwagon l in L) encoded.AddRange(l.ToBytes());

        foreach (Banderwagon r in R) encoded.AddRange(r.ToBytes());

        encoded.AddRange(A.ToBytes());

        return encoded.ToArray();
    }

    public override string ToString()
    {
        StringBuilder stringBuilder = new();
        stringBuilder.Append("\n#[_l]#\n");
        foreach (Banderwagon l in L)
        {
            stringBuilder.AppendJoin(", ", l.ToBytes());
            stringBuilder.Append('\n');
        }

        stringBuilder.Append("\n#[_a]#\n");
        stringBuilder.AppendJoin(", ", A.ToBytes());
        stringBuilder.Append("\n#[_r]#\n");
        foreach (Banderwagon l in R)
        {
            stringBuilder.AppendJoin(", ", l.ToBytes());
            stringBuilder.Append('\n');
        }

        return stringBuilder.ToString();
    }
}

public readonly struct IpaProofStructSerialized(byte[][] l, byte[] a, byte[][] r)
{
    public readonly byte[] A = a;
    public readonly byte[][] L = l;
    public readonly byte[][] R = r;

    public static IpaProofStructSerialized CreateIpaProofSerialized(byte[] proof)
    {
        int startIndex = 64;

        byte[][] l = new byte[8][];
        byte[][] r = new byte[8][];
        byte[] a = proof[1088..1120];

        for (int i = 0; i < 8; i++)
        {
            int sliceStartL = startIndex + i * 64;
            int sliceStartR = startIndex + 512 + i * 64;

            l[i] = new byte[64];
            r[i] = new byte[64];
            Array.Copy(proof, sliceStartL, l[i], 0, 64);
            Array.Copy(proof, sliceStartR, r[i], 0, 64);
        }
        return new IpaProofStructSerialized(l, a, r);
    }

    public byte[] Encode()
    {
        List<byte> encoded = [];
        foreach (byte[] l in L) encoded.AddRange(l);
        foreach (byte[] r in R) encoded.AddRange(r);
        encoded.AddRange(A);
        return encoded.ToArray();
    }

    public override string ToString()
    {
        StringBuilder stringBuilder = new StringBuilder();
        stringBuilder.Append("\n#[_l]#\n");
        foreach (byte[] l in L)
        {
            stringBuilder.AppendJoin(", ", l.Select(b => b.ToString("X2")));
            stringBuilder.Append('\n');
        }
        stringBuilder.Append("\n#[_a]#\n");
        stringBuilder.AppendJoin(", ", A);
        stringBuilder.Append("\n#[_r]#\n");
        foreach (byte[] r in R)
        {
            stringBuilder.AppendJoin(", ", r.Select(b => b.ToString("X2")));
            stringBuilder.Append('\n');
        }
        return stringBuilder.ToString();
    }
}
public readonly struct VerkleProofStruct
{
    public readonly Banderwagon D;
    public readonly IpaProofStruct IpaProof;

    public VerkleProofStruct(IpaProofStruct ipaProof, Banderwagon d)
    {
        IpaProof = ipaProof;
        D = d;
    }

    public override string ToString()
    {
        StringBuilder stringBuilder = new();
        stringBuilder.Append("\n##[IPA Proof]##\n");
        stringBuilder.Append(IpaProof);
        stringBuilder.Append("\n##[_d]##\n");
        stringBuilder.AppendJoin(", ", D.ToBytes());
        return stringBuilder.ToString();
    }

    public byte[] Encode()
    {
        List<byte> encoded = [];

        encoded.AddRange(D.ToBytes());
        encoded.AddRange(IpaProof.Encode());

        return encoded.ToArray();
    }
}

public readonly struct VerkleProofStructSerialized(IpaProofStructSerialized ipaProofSerialized, byte[] d)
{
    public readonly byte[] D = d;
    public readonly IpaProofStructSerialized IpaProofSerialized = ipaProofSerialized;

    public byte[] Encode()
    {
        List<byte> encoded = [.. D, .. IpaProofSerialized.Encode()];
        return encoded.ToArray();
    }

    public override string ToString()
    {
        StringBuilder stringBuilder = new StringBuilder();
        stringBuilder.Append("\n#[_d]#\n");
        stringBuilder.AppendJoin(", ", D);
        stringBuilder.Append(IpaProofSerialized.ToString());
        return stringBuilder.ToString();
    }
}
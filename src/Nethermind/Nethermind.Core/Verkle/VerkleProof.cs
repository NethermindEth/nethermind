// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Text;
using FastEnumUtility;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Fields.FrEElement;
using Nethermind.Verkle.Proofs;

namespace Nethermind.Core.Verkle;

public enum ExtPresent : byte
{
    None = 0,
    DifferentStem = 1,
    Present = 2
}

public struct VerkleProof
{
    public VerificationHint VerifyHint;
    public byte[][] CommsSorted;
    public VerkleProofStructSerialized Proof;

    public override string ToString()
    {
        StringBuilder stringBuilder = new StringBuilder();
        stringBuilder.Append("\n####[Verkle Proof]####\n");
        stringBuilder.Append("\n###[Verify Hint]###\n");
        stringBuilder.Append(VerifyHint.ToString());
        stringBuilder.Append("\n###[Comms Sorted]###\n");
        foreach (byte[] comm in CommsSorted)
        {
            stringBuilder.AppendJoin(", ", comm.Reverse().ToArray());
            stringBuilder.Append('\n');
        }
        stringBuilder.Append("\n###[Inner Proof]###\n");
        stringBuilder.Append(Proof.ToString());
        return stringBuilder.ToString();
    }

    public byte[] Encode()
    {
        var encoded = new List<byte>(VerifyHint.Encode());

        encoded.AddRange(CommsSorted.Length.ToByteArrayLittleEndian());
        foreach (byte[] comm in CommsSorted)
        {
            encoded.AddRange(comm.Reverse());
        }

        encoded.AddRange(Proof.Encode());

        return encoded.ToArray();
    }

    public static VerkleProof Decode(byte[] proof)
    {
        return new VerkleProof();
    }
}

public struct VerificationHint
{
    public byte[] Depths;
    public ExtPresent[] ExtensionPresent;
    public byte[][] DifferentStemNoProof;

    public override string ToString()
    {
        StringBuilder stringBuilder = new StringBuilder();
        stringBuilder.Append("\n##[Depths]##\n");
        stringBuilder.AppendJoin(", ", Depths);
        stringBuilder.Append("\n##[ExtensionPresent]##\n");
        stringBuilder.AppendJoin(", ", ExtensionPresent.Select(x => x.ToString()));
        stringBuilder.Append("\n##[DifferentStemNoProof]##\n");
        foreach (byte[] stem in DifferentStemNoProof)
        {
            stringBuilder.AppendJoin(", ", stem);
        }
        return stringBuilder.ToString();
    }

    public byte[] Encode()
    {
        List<byte> encoded = new List<byte>();

        encoded.AddRange(DifferentStemNoProof.Length.ToByteArrayLittleEndian());
        foreach (byte[] stem in DifferentStemNoProof)
        {
            encoded.AddRange(stem);
        }

        encoded.AddRange(Depths.Length.ToByteArrayLittleEndian());

        foreach ((byte depth, ExtPresent extPresent) in Depths.Zip(ExtensionPresent))
        {
            byte extPresentByte = (byte)(extPresent.ToByte() | (depth << 3));
            encoded.Add(extPresentByte);
        }

        return encoded.ToArray();
    }
}

public struct UpdateHint
{
    public Dictionary<Stem, (ExtPresent, byte)> DepthAndExtByStem { get; set; }
    public SpanDictionary<byte, byte[]> CommByPath { get; set; }
    public SortedDictionary<byte[], Stem> DifferentStemNoProof { get; set; }
}

public struct SuffixPoly
{
    public FrE[] C1 { get; set; }
    public FrE[] C2 { get; set; }
}

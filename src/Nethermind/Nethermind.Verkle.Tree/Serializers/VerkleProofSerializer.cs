// SPDX-FileCopyrightText:2023 Demerzel Solutions Limited
// SPDX-License-Identifier:LGPL-3.0-only

using System;
using FastEnumUtility;
using Nethermind.Core.Verkle;
using Nethermind.Serialization.Rlp;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Fields.FrEElement;
using Nethermind.Verkle.Proofs;

namespace Nethermind.Verkle.Tree.Serializers;

public class VerkleProofSerializer : IRlpStreamDecoder<VerkleProofSerialized>
{
    public static VerkleProofSerializer Instance = new VerkleProofSerializer();
    public int GetLength(VerkleProofSerialized item, RlpBehaviors rlpBehaviors)
    {
        int contentLength = 0;

        int verkleProofStructLength = 0;
        int ipaLength = 0;
        ipaLength += 33; //Rlp.LengthOf(FrE A)
        ipaLength += Rlp.LengthOfSequence(item.Proof.IpaProofSerialized.L.Length * 66) +
                     Rlp.LengthOfSequence(item.Proof.IpaProofSerialized.R.Length * 66);
        verkleProofStructLength += Rlp.LengthOfSequence(ipaLength);
        verkleProofStructLength += 66;

        contentLength += Rlp.LengthOfSequence(verkleProofStructLength);

        contentLength += Rlp.LengthOfSequence(33 * item.CommsSorted.Length);

        int hintLength = 0;
        hintLength += Rlp.LengthOfSequence(item.VerifyHint.DifferentStemNoProof.Length * 32);
        hintLength += Rlp.LengthOfSequence(item.VerifyHint.Depths.Length);
        contentLength += Rlp.LengthOfSequence(hintLength);

        return contentLength;
    }

    public VerkleProofSerialized Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        rlpStream.ReadSequenceLength();
        VerkleProofStructSerialized proofStruct = DecodeVerkleProofStruct(rlpStream);
        Banderwagon[] comsSorted = rlpStream.DecodeArray(DecodeBanderwagon);
        VerificationHint hint = DecodeVerifyHint(rlpStream);
        return new VerkleProofSerialized()
        {
            CommsSorted = comsSorted,
            Proof = proofStruct,
            VerifyHint = hint
        };
    }

    private VerkleProofStructSerialized DecodeVerkleProofStruct(RlpStream stream)
    {
        stream.ReadSequenceLength();
        IpaProofStructSerialized proofStruct = DecodeIpaProofStruct(stream);
        byte[] d = stream.DecodeByteArray();
        return new VerkleProofStructSerialized(proofStruct, d);
    }

    private IpaProofStructSerialized DecodeIpaProofStruct(RlpStream stream)
    {
        stream.ReadSequenceLength();
        byte[] a = stream.DecodeByteArray();
        byte[][] cl = stream.DecodeArray(DecodeBanderwagonS);
        byte[][] cr = stream.DecodeArray(DecodeBanderwagonS);
        return new IpaProofStructSerialized(cl, a, cr);
    }

    private VerificationHint DecodeVerifyHint(RlpStream stream)
    {
        stream.ReadSequenceLength();
        byte[][] diffStem = stream.DecodeArray(s => s.DecodeByteArray());
        var depthData = stream.DecodeByteArray();
        ExtPresent[] extPresent = new ExtPresent[depthData.Length];
        byte[] depth = new byte[depthData.Length];
        for (int i = 0; i < depthData.Length; i++)
        {
            depth[i] = (byte)(depthData[i] >> 3);
            extPresent[i] = (ExtPresent)(depthData[i] ^ (byte)(depth[i] << 3));
        }

        return new VerificationHint()
        {
            DifferentStemNoProof = diffStem,
            ExtensionPresent = extPresent,
            Depths = depth
        };
    }

    private Banderwagon DecodeBanderwagon(RlpStream stream)
    {
        return Banderwagon.FromBytes(stream.DecodeByteArray(), subgroupCheck: false)!.Value;
    }

    private byte[] DecodeBanderwagonS(RlpStream stream)
    {
        return stream.DecodeByteArray();
    }

    public void Encode(RlpStream stream, VerkleProofSerialized item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        var contentLength = GetLength(item, rlpBehaviors);
        stream.StartSequence(contentLength);

        int verkleProofStructLength = 0;
        int ipaLength = 0;
        ipaLength += 33; //Rlp.LengthOf(FrE A)
        ipaLength += Rlp.LengthOfSequence(item.Proof.IpaProofSerialized.L.Length * 66) +
                     Rlp.LengthOfSequence(item.Proof.IpaProofSerialized.R.Length * 66);
        verkleProofStructLength += Rlp.LengthOfSequence(ipaLength);
        verkleProofStructLength += 65;
        stream.StartSequence(verkleProofStructLength);
        stream.StartSequence(ipaLength);
        stream.Encode(item.Proof.IpaProofSerialized.A);
        stream.StartSequence(item.Proof.IpaProofSerialized.L.Length * 66);
        foreach (byte[] data in item.Proof.IpaProofSerialized.L) stream.Encode(data);
        stream.StartSequence(item.Proof.IpaProofSerialized.R.Length * 66);
        foreach (byte[] data in item.Proof.IpaProofSerialized.R) stream.Encode(data);
        stream.Encode(item.Proof.D);

        if (item.CommsSorted.Length == 0)
        {
            stream.EncodeNullObject();
        }
        else
        {
            stream.StartSequence(33 * item.CommsSorted.Length);
            foreach (var data in item.CommsSorted) stream.Encode(data.ToBytes());
        }

        int hintLength = 0;
        hintLength += Rlp.LengthOfSequence(item.VerifyHint.DifferentStemNoProof.Length * 32);
        hintLength += Rlp.LengthOfSequence(item.VerifyHint.Depths.Length);
        stream.StartSequence(hintLength);
        if (item.VerifyHint.DifferentStemNoProof.Length == 0)
        {
            stream.EncodeNullObject();
        }
        else
        {
            stream.StartSequence(item.VerifyHint.DifferentStemNoProof.Length * 32);
            foreach (var data in item.VerifyHint.DifferentStemNoProof) stream.Encode(data);
        }
        byte[] encodedDepthExtension = new byte[item.VerifyHint.Depths.Length];
        for (int i = 0; i < item.VerifyHint.Depths.Length; i++)
        {
            encodedDepthExtension[i] = (byte)(item.VerifyHint.ExtensionPresent[i].ToByte() | (item.VerifyHint.Depths[i] << 3));
        }
        stream.Encode(encodedDepthExtension);
    }
}

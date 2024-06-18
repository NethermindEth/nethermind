// SPDX-FileCopyrightText:2023 Demerzel Solutions Limited
// SPDX-License-Identifier:LGPL-3.0-only

using FastEnumUtility;
using Nethermind.Core.Verkle;
using Nethermind.Serialization.Rlp;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Fields.FrEElement;
using Nethermind.Verkle.Proofs;

namespace Nethermind.Verkle.Tree.Serializers;

public class VerkleProofSerializer : IRlpStreamDecoder<VerkleProof>
{
    public static VerkleProofSerializer Instance = new VerkleProofSerializer();
    public int GetLength(VerkleProof item, RlpBehaviors rlpBehaviors)
    {
        int contentLength = 0;

        int verkleProofStructLength = 0;
        int ipaLength = 0;
        ipaLength += 33; //Rlp.LengthOf(FrE A)
        ipaLength += Rlp.LengthOfSequence(item.Proof.IpaProofSerialized.L.Length * 33) +
                     Rlp.LengthOfSequence(item.Proof.IpaProofSerialized.R.Length * 33);
        verkleProofStructLength += Rlp.LengthOfSequence(ipaLength);
        verkleProofStructLength += 33;

        contentLength += Rlp.LengthOfSequence(verkleProofStructLength);

        contentLength += Rlp.LengthOfSequence(33 * item.CommsSorted.Length);

        int hintLength = 0;
        hintLength += Rlp.LengthOfSequence(item.VerifyHint.DifferentStemNoProof.Length * 32);
        hintLength += Rlp.LengthOfSequence(item.VerifyHint.Depths.Length);
        contentLength += Rlp.LengthOfSequence(hintLength);

        return contentLength;
    }

    public VerkleProof Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        rlpStream.ReadSequenceLength();
        VerkleProofStructSerialized proofStruct = DecodeVerkleProofStruct(rlpStream);
        byte[][] comsSorted = rlpStream.DecodeArray(DecodeBanderwagon);
        VerificationHint hint = DecodeVerifyHint(rlpStream);
        return new VerkleProof()
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
        byte[] d = Banderwagon.FromBytesUncompressedUnchecked(stream.DecodeByteArray(), isBigEndian: false).ToBytesUncompressedLittleEndian();
        return new VerkleProofStructSerialized(proofStruct, d);
    }

    private IpaProofStructSerialized DecodeIpaProofStruct(RlpStream stream)
    {
        stream.ReadSequenceLength();
        var a = FrE.FromBytes(stream.DecodeByteArray());
        byte[][] cl = stream.DecodeArray(DecodeBanderwagon);
        byte[][] cr = stream.DecodeArray(DecodeBanderwagon);
        return new IpaProofStructSerialized(cl, a.ToBytes(), cr);
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

    private byte[] DecodeBanderwagon(RlpStream stream)
    {
        return Banderwagon.FromBytesUncompressedUnchecked(stream.DecodeByteArray(), isBigEndian: false).ToBytesUncompressedLittleEndian();
    }

    public void Encode(RlpStream stream, VerkleProof item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        var contentLength = GetLength(item, rlpBehaviors);
        stream.StartSequence(contentLength);

        int verkleProofStructLength = 0;
        int ipaLength = 0;
        ipaLength += 33; //Rlp.LengthOf(FrE A)
        ipaLength += Rlp.LengthOfSequence(item.Proof.IpaProofSerialized.L.Length * 33) +
                     Rlp.LengthOfSequence(item.Proof.IpaProofSerialized.R.Length * 33);
        verkleProofStructLength += Rlp.LengthOfSequence(ipaLength);
        verkleProofStructLength += 33;
        stream.StartSequence(verkleProofStructLength);
        stream.StartSequence(ipaLength);
        stream.Encode(item.Proof.IpaProofSerialized.A);
        stream.StartSequence(item.Proof.IpaProofSerialized.L.Length * 33);
        foreach (byte[] data in item.Proof.IpaProofSerialized.L) stream.Encode(data);
        stream.StartSequence(item.Proof.IpaProofSerialized.R.Length * 33);
        foreach (byte[] data in item.Proof.IpaProofSerialized.R) stream.Encode(data);
        stream.Encode(item.Proof.D);

        if (item.CommsSorted.Length == 0)
        {
            stream.EncodeNullObject();
        }
        else
        {
            stream.StartSequence(33 * item.CommsSorted.Length);
            foreach (var data in item.CommsSorted) stream.Encode(data);
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

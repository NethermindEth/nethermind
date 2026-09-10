// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Qbft.Bft;

/// <summary>
/// QBFT extra data: <c>RLP([vanity, [validators...], vote | [], round, [seals...]])</c>.
/// </summary>
/// <remarks>
/// An absent vote is an empty list, a vote is <c>[recipient, 0xFF]</c> (add) or <c>[recipient, ""]</c> (drop),
/// the round is a minimal scalar and the omitted seals are written as an empty list so that the
/// structure always has five items. Matches Besu's <c>QbftExtraDataCodec</c>.
/// </remarks>
public sealed class QbftExtraDataCodec : IBftExtraDataCodec
{
    public static readonly QbftExtraDataCodec Instance = new();

    public byte[] Encode(BftExtraData extraData, BftExtraDataEncoding encoding = BftExtraDataEncoding.All)
    {
        bool writeSeals = encoding == BftExtraDataEncoding.All;
        int round = encoding == BftExtraDataEncoding.ExcludeCommitSealsAndRound ? 0 : extraData.Round;
        byte[][] seals = writeSeals ? BftExtraDataRlp.EncodeSeals(extraData.Seals) : [];

        int validatorsContentLength = BftExtraDataRlp.ValidatorsContentLength(extraData.Validators);
        int voteLength = extraData.Vote is null ? 1 : Rlp.LengthOfSequence(VoteContentLength(extraData.Vote));
        int sealsContentLength = BftExtraDataRlp.SealsContentLength(seals);
        int contentLength = Rlp.LengthOf(extraData.VanityData)
                            + Rlp.LengthOfSequence(validatorsContentLength)
                            + voteLength
                            + Rlp.LengthOf(round)
                            + Rlp.LengthOfSequence(sealsContentLength);

        byte[] result = new byte[Rlp.LengthOfSequence(contentLength)];
        RlpWriter writer = new(result);
        writer.StartSequence(contentLength);
        writer.Encode(extraData.VanityData);
        BftExtraDataRlp.EncodeValidators(ref writer, extraData.Validators, validatorsContentLength);
        if (extraData.Vote is null)
        {
            writer.EncodeNullObject();
        }
        else
        {
            EncodeVote(ref writer, extraData.Vote);
        }

        writer.Encode(round);
        BftExtraDataRlp.EncodeSeals(ref writer, seals, sealsContentLength);
        return result;
    }

    public BftExtraData Decode(ReadOnlySpan<byte> extraData)
    {
        if (extraData.IsEmpty)
        {
            throw new RlpException("Invalid bytes supplied - BFT extra data required.");
        }

        RlpReader reader = new(extraData);
        int end = reader.ReadSequenceLength() + reader.Position;
        byte[] vanity = reader.DecodeByteArray();
        Address[] validators = BftExtraDataRlp.DecodeValidators(ref reader);

        Vote? vote;
        if (reader.IsNextItemEmptyList())
        {
            reader.ReadByte();
            vote = null;
        }
        else
        {
            vote = DecodeVote(ref reader);
        }

        int round = reader.DecodeInt();
        Signature[] seals = BftExtraDataRlp.DecodeSeals(ref reader);
        reader.Check(end);
        return new BftExtraData(vanity, seals, vote, round, validators);
    }

    private static int VoteContentLength(Vote vote) => Rlp.LengthOfAddressRlp + (vote.IsAdd ? 2 : 1);

    private static void EncodeVote(ref RlpWriter writer, Vote vote)
    {
        writer.StartSequence(VoteContentLength(vote));
        writer.Encode(vote.Recipient);
        if (vote.IsAdd)
        {
            writer.Encode(Vote.AddByteValue);
        }
        else
        {
            writer.EncodeEmptyByteArray();
        }
    }

    private static Vote DecodeVote(ref RlpReader reader)
    {
        int length = reader.ReadSequenceLength();
        int end = reader.Position + length;
        Address recipient = reader.DecodeAddress();
        VoteType type;
        if (reader.IsNextItemEmptyByteArray())
        {
            reader.ReadByte();
            type = VoteType.Drop;
        }
        else
        {
            byte value = reader.DecodeByte();
            type = value == Vote.AddByteValue ? VoteType.Add : throw new RlpException("Vote field was of an incorrect binary value.");
        }

        reader.Check(end);
        return new Vote(recipient, type);
    }
}

/// <summary>RLP helpers shared by the QBFT and IBFT 2.0 extra data codecs.</summary>
internal static class BftExtraDataRlp
{
    public static int ValidatorsContentLength(IReadOnlyList<Address> validators) => validators.Count * Rlp.LengthOfAddressRlp;

    public static void EncodeValidators(ref RlpWriter writer, IReadOnlyList<Address> validators, int contentLength)
    {
        writer.StartSequence(contentLength);
        for (int i = 0; i < validators.Count; i++)
        {
            writer.Encode(validators[i]);
        }
    }

    public static Address[] DecodeValidators(ref RlpReader reader)
    {
        int length = reader.ReadSequenceLength();
        int end = reader.Position + length;
        List<Address> validators = new(length / Rlp.LengthOfAddressRlp);
        while (reader.Position < end)
        {
            validators.Add(reader.DecodeAddress());
        }

        reader.Check(end);
        return validators.ToArray();
    }

    public static byte[][] EncodeSeals(IReadOnlyList<Signature> seals)
    {
        byte[][] encoded = new byte[seals.Count][];
        for (int i = 0; i < seals.Count; i++)
        {
            encoded[i] = BftSignatures.Encode(seals[i]);
        }

        return encoded;
    }

    public static int SealsContentLength(byte[][] seals)
    {
        int length = 0;
        for (int i = 0; i < seals.Length; i++)
        {
            length += Rlp.LengthOf(seals[i]);
        }

        return length;
    }

    public static void EncodeSeals(ref RlpWriter writer, byte[][] seals, int contentLength)
    {
        writer.StartSequence(contentLength);
        for (int i = 0; i < seals.Length; i++)
        {
            writer.Encode(seals[i]);
        }
    }

    public static Signature[] DecodeSeals(ref RlpReader reader)
    {
        int length = reader.ReadSequenceLength();
        int end = reader.Position + length;
        List<Signature> seals = [];
        while (reader.Position < end)
        {
            seals.Add(BftSignatures.Decode(reader.DecodeByteArraySpan()));
        }

        reader.Check(end);
        return seals.ToArray();
    }
}

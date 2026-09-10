// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Qbft.Bft;

/// <summary>
/// IBFT 2.0 extra data: <c>RLP([vanity, [validators...], vote | "", round(4 bytes), [seals...]])</c>.
/// </summary>
/// <remarks>
/// Only used to validate the pre-migration blocks of a chain that moved from IBFT 2.0 to QBFT.
/// Differs from <see cref="QbftExtraDataCodec"/> in three places: an absent vote is an empty string,
/// the round is a fixed 4-byte big-endian value, and the digests <em>omit</em> the trailing items
/// rather than writing them empty. Matches Besu's <c>IbftExtraDataCodec</c>.
/// </remarks>
public sealed class Ibft2ExtraDataCodec : IBftExtraDataCodec
{
    public static readonly Ibft2ExtraDataCodec Instance = new();

    private const int RoundLength = 4;
    private static readonly byte[] DropVoteBytes = [Vote.DropByteValue];

    public byte[] Encode(BftExtraData extraData, BftExtraDataEncoding encoding = BftExtraDataEncoding.All)
    {
        bool writeRound = encoding != BftExtraDataEncoding.ExcludeCommitSealsAndRound;
        bool writeSeals = encoding == BftExtraDataEncoding.All;
        byte[][] seals = writeSeals ? BftExtraDataRlp.EncodeSeals(extraData.Seals) : [];

        int validatorsContentLength = BftExtraDataRlp.ValidatorsContentLength(extraData.Validators);
        int voteLength = extraData.Vote is null ? 1 : Rlp.LengthOfSequence(VoteContentLength(extraData.Vote));
        int sealsContentLength = BftExtraDataRlp.SealsContentLength(seals);
        int contentLength = Rlp.LengthOf(extraData.VanityData)
                            + Rlp.LengthOfSequence(validatorsContentLength)
                            + voteLength
                            + (writeRound ? 1 + RoundLength : 0)
                            + (writeSeals ? Rlp.LengthOfSequence(sealsContentLength) : 0);

        byte[] result = new byte[Rlp.LengthOfSequence(contentLength)];
        RlpWriter writer = new(result);
        writer.StartSequence(contentLength);
        writer.Encode(extraData.VanityData);
        BftExtraDataRlp.EncodeValidators(ref writer, extraData.Validators, validatorsContentLength);
        if (extraData.Vote is null)
        {
            writer.EncodeEmptyByteArray();
        }
        else
        {
            writer.StartSequence(VoteContentLength(extraData.Vote));
            writer.Encode(extraData.Vote.Recipient);
            if (extraData.Vote.IsAdd)
            {
                writer.Encode(Vote.AddByteValue);
            }
            else
            {
                // Besu writes the drop vote as the raw byte 0x00, which is also the RLP of the one-byte string 0x00.
                writer.Encode(DropVoteBytes);
            }
        }

        if (writeRound)
        {
            Span<byte> round = stackalloc byte[RoundLength];
            BinaryPrimitives.WriteInt32BigEndian(round, extraData.Round);
            writer.Encode(round);
        }

        if (writeSeals)
        {
            BftExtraDataRlp.EncodeSeals(ref writer, seals, sealsContentLength);
        }

        return result;
    }

    public BftExtraData Decode(ReadOnlySpan<byte> extraData)
    {
        if (extraData.IsEmpty)
        {
            throw new RlpException("Invalid bytes supplied - BFT extra data required.");
        }

        RlpReader reader = new(extraData);
        reader.ReadSequenceLength();
        byte[] vanity = reader.DecodeByteArray();
        Address[] validators = BftExtraDataRlp.DecodeValidators(ref reader);

        Vote? vote;
        if (reader.IsNextItemEmptyByteArray())
        {
            reader.ReadByte();
            vote = null;
        }
        else
        {
            int length = reader.ReadSequenceLength();
            int end = reader.Position + length;
            Address recipient = reader.DecodeAddress();
            VoteType type;
            if (reader.PeekByte() == Vote.DropByteValue)
            {
                reader.ReadByte();
                type = VoteType.Drop;
            }
            else
            {
                type = reader.DecodeByte() == Vote.AddByteValue ? VoteType.Add : throw new RlpException("Vote field was of an incorrect binary value.");
            }

            reader.Check(end);
            vote = new Vote(recipient, type);
        }

        ReadOnlySpan<byte> roundBytes = reader.DecodeByteArraySpan();
        if (roundBytes.Length != RoundLength)
        {
            throw new RlpException($"IBFT round must be {RoundLength} bytes, got {roundBytes.Length}.");
        }

        int round = BinaryPrimitives.ReadInt32BigEndian(roundBytes);
        Signature[] seals = BftExtraDataRlp.DecodeSeals(ref reader);
        return new BftExtraData(vanity, seals, vote, round, validators);
    }

    private static int VoteContentLength(Vote vote) => Rlp.LengthOfAddressRlp + (vote.IsAdd ? 2 : 1);
}

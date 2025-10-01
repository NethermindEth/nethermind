// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc.RLP;

internal class XdcBlockInfoDecoder : IRlpValueDecoder<BlockRoundInfo>, IRlpStreamDecoder<BlockRoundInfo>
{
    public BlockRoundInfo Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (decoderContext.IsNextItemNull())
            return null;
        int sequenceLength = decoderContext.ReadSequenceLength();
        int endPosition = decoderContext.Position + sequenceLength;

        byte[] hashBytes = decoderContext.DecodeByteArray();
        if (hashBytes.Length > Hash256.Size)
            throw new RlpException($"Hash length {hashBytes.Length} is longer than max size of 32.");
        ulong round = decoderContext.DecodeULong();
        long number = decoderContext.DecodeLong();

        return new BlockRoundInfo(new Hash256(hashBytes), round, number);

    }

    public BlockRoundInfo Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (rlpStream.IsNextItemNull())
            return null;
        int sequenceLength = rlpStream.ReadSequenceLength();
        int endPosition = rlpStream.Position + sequenceLength;

        byte[] hashBytes = rlpStream.DecodeByteArray();
        if (hashBytes.Length > Hash256.Size)
            throw new RlpException($"Hash length {hashBytes.Length} is longer than max size of 32.");
        ulong round = rlpStream.DecodeULong();
        long number = rlpStream.DecodeLong();

        return new BlockRoundInfo(new Hash256(hashBytes), round, number);
    }

    public void Encode(RlpStream stream, BlockRoundInfo item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            stream.EncodeNullObject();
            return;
        }
        stream.StartSequence(GetContentLength(item, rlpBehaviors));
        stream.Encode(item.Hash);
        stream.Encode(item.Round);
        stream.Encode(item.BlockNumber);
    }

    public int GetLength(BlockRoundInfo item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        return Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));
    }

    private static int GetContentLength(BlockRoundInfo? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
            return 0;
        return Rlp.LengthOf(item.Hash) +
               Rlp.LengthOf(item.Round) +
               Rlp.LengthOf(item.BlockNumber);
    }

}

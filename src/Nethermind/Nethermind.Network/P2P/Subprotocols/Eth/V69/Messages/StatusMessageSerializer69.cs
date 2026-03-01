// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;

public class StatusMessageSerializer69 :
    IZeroInnerMessageSerializer<StatusMessage69>
{
    private const int ForkHashLength = 5;

    public void Serialize(IByteBuffer byteBuffer, StatusMessage69 message)
    {
        NettyRlpStream rlpStream = new(byteBuffer);

        int totalLength = GetLength(message, out int contentLength);
        byteBuffer.EnsureWritable(totalLength);
        rlpStream.StartSequence(contentLength);

        rlpStream.Encode(message.ProtocolVersion);
        rlpStream.Encode(message.NetworkId);
        rlpStream.Encode(message.GenesisHash);
        EncodeForkId(rlpStream, message.ForkId);
        rlpStream.Encode(message.EarliestBlock);
        rlpStream.Encode(message.LatestBlock);
        rlpStream.Encode(message.LatestBlockHash);
    }

    public int GetLength(StatusMessage69 message, out int contentLength)
    {
        contentLength =
            Rlp.LengthOf(message.ProtocolVersion) +
            Rlp.LengthOf(message.NetworkId) +
            Rlp.LengthOf(message.GenesisHash) +
            LengthOfForkId(message.ForkId) +
            Rlp.LengthOf(message.EarliestBlock) +
            Rlp.LengthOf(message.LatestBlock) +
            Rlp.LengthOf(message.LatestBlockHash);

        return Rlp.LengthOfSequence(contentLength);
    }

    public StatusMessage69 Deserialize(IByteBuffer byteBuffer)
    {
        Rlp.ValueDecoderContext ctx = byteBuffer.AsRlpContext();
        ctx.ReadSequenceLength();

        StatusMessage69 statusMessage = new()
        {
            ProtocolVersion = ctx.DecodeByte(),
            NetworkId = ctx.DecodeUInt256(),
            GenesisHash = ctx.DecodeKeccak() ?? Hash256.Zero,
            ForkId = DecodeForkId(ref ctx),
            EarliestBlock = ctx.DecodeLong(),
            LatestBlock = ctx.DecodeLong(),
            LatestBlockHash = ctx.DecodeKeccak() ?? Hash256.Zero
        };

        byteBuffer.SetReaderIndex(byteBuffer.ReaderIndex + ctx.Position);
        return statusMessage;
    }

    private static void EncodeForkId(RlpStream rlpStream, ForkId forkId)
    {
        var forkIdContentLength = ForkHashLength + Rlp.LengthOf(forkId.Next);
        rlpStream.StartSequence(forkIdContentLength);
        rlpStream.Encode(forkId.HashBytes);
        rlpStream.Encode(forkId.Next);
    }

    private static ForkId DecodeForkId(ref Rlp.ValueDecoderContext ctx)
    {
        ctx.ReadSequenceLength();
        uint forkHash = (uint)ctx.DecodeUInt256(ForkHashLength - 1);
        ulong next = ctx.DecodeULong();
        return new(forkHash, next);
    }

    private static int LengthOfForkId(ForkId forkId)
    {
        int forkIdContentLength = ForkHashLength + Rlp.LengthOf(forkId.Next);
        return Rlp.LengthOfSequence(forkIdContentLength);
    }
}

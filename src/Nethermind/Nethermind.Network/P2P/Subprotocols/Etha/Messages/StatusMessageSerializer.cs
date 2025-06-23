// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Etha.Messages;

public class StatusMessageSerializer :
    IZeroInnerMessageSerializer<StatusMessage>
{
    private const int ForkHashLength = 5;

    public void Serialize(IByteBuffer byteBuffer, StatusMessage message)
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
        rlpStream.Encode(message.BlockBitmask);
    }

    public int GetLength(StatusMessage message, out int contentLength)
    {
        contentLength =
            Rlp.LengthOf(message.ProtocolVersion) +
            Rlp.LengthOf(message.NetworkId) +
            Rlp.LengthOf(message.GenesisHash) +
            LengthOfForkId(message.ForkId) +
            Rlp.LengthOf(message.EarliestBlock) +
            Rlp.LengthOf(message.LatestBlock) +
            Rlp.LengthOf(message.LatestBlock) +
            Rlp.LengthOf(message.LatestBlockHash) +
            // Rlp.LengthOf((uint)message.BlockBitmask);
            Rlp.LengthOf((long)message.BlockBitmask);

        return Rlp.LengthOfSequence(contentLength);
    }

    public StatusMessage Deserialize(IByteBuffer byteBuffer)
    {
        RlpStream rlpStream = new NettyRlpStream(byteBuffer);
        rlpStream.ReadSequenceLength();

        StatusMessage statusMessage = new()
        {
            ProtocolVersion = rlpStream.DecodeByte(),
            NetworkId = rlpStream.DecodeUInt256(),
            GenesisHash = rlpStream.DecodeKeccak() ?? Hash256.Zero,
            ForkId = DecodeForkId(rlpStream),
            EarliestBlock = rlpStream.DecodeLong(),
            LatestBlock = rlpStream.DecodeLong(),
            LatestBlockHash = rlpStream.DecodeKeccak() ?? Hash256.Zero,
            BlockBitmask = rlpStream.DecodeUInt(),
        };

        return statusMessage;
    }

    private static void EncodeForkId(RlpStream rlpStream, ForkId forkId)
    {
        var forkIdContentLength = ForkHashLength + Rlp.LengthOf(forkId.Next);
        rlpStream.StartSequence(forkIdContentLength);
        rlpStream.Encode(forkId.HashBytes);
        rlpStream.Encode(forkId.Next);
    }

    private static ForkId DecodeForkId(RlpStream rlpStream)
    {
        rlpStream.ReadSequenceLength();
        uint forkHash = (uint)rlpStream.DecodeUInt256(ForkHashLength - 1);
        ulong next = rlpStream.DecodeUlong();
        return new(forkHash, next);
    }

    private static int LengthOfForkId(ForkId forkId)
    {
        int forkIdContentLength = ForkHashLength + Rlp.LengthOf(forkId.Next);
        return Rlp.LengthOfSequence(forkIdContentLength);
    }
}

// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;

public class StatusMessageSerializer69 :
    IZeroMessageSerializer<StatusMessage69>
{
    private const int ForkHashLength = 5;

    public void Serialize(Span<byte> buffer, StatusMessage69 message)
    {
        GetLength(message, out int contentLength);
        RlpWriter writer = new(buffer);
        writer.StartSequence(contentLength);

        writer.Encode(message.ProtocolVersion);
        writer.Encode(message.NetworkId);
        writer.Encode(message.GenesisHash);
        EncodeForkId(ref writer, message.ForkId);
        writer.Encode(message.EarliestBlock);
        writer.Encode(message.LatestBlock);
        writer.Encode(message.LatestBlockHash);
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

    public StatusMessage69 Deserialize(ReadOnlySpan<byte> data, out int consumed)
    {
        RlpReader ctx = new(data);
        StatusMessage69 msg = Deserialize(ref ctx);
        consumed = ctx.Position;
        return msg;
    }

    private static StatusMessage69 Deserialize(ref RlpReader ctx)
    {
        ctx.ReadSequenceLength();

        return new StatusMessage69
        {
            ProtocolVersion = ctx.DecodeByte(),
            NetworkId = ctx.DecodeULong(),
            GenesisHash = ctx.DecodeKeccak(),
            ForkId = DecodeForkId(ref ctx),
            EarliestBlock = ctx.DecodeULong(),
            LatestBlock = ctx.DecodeULong(),
            LatestBlockHash = ctx.DecodeKeccak()
        };
    }

    private static void EncodeForkId<TWriter>(ref TWriter writer, ForkId forkId)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        int forkIdContentLength = ForkHashLength + Rlp.LengthOf(forkId.Next);
        writer.StartSequence(forkIdContentLength);
        writer.Encode(forkId.HashBytes);
        writer.Encode(forkId.Next);
    }

    private static ForkId DecodeForkId(ref RlpReader ctx)
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

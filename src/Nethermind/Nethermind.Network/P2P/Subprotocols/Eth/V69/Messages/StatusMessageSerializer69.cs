// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using DotNetty.Buffers;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;

public class StatusMessageSerializer69 :
    IZeroInnerMessageSerializer<StatusMessage69>
{
    private const int ForkHashLength = 5;

    public void Serialize(IByteBuffer byteBuffer, StatusMessage69 message)
    {
        int totalLength = GetLength(message, out int contentLength);
        RlpWriter writer = byteBuffer.StartRlpWrite(totalLength);
        RlpSequence sequence = writer.StartSequence(contentLength);

        writer.Encode(message.ProtocolVersion);
        writer.Encode(message.NetworkId);
        writer.Encode(message.GenesisHash);
        EncodeForkId(ref writer, message.ForkId);
        writer.Encode(message.EarliestBlock);
        writer.Encode(message.LatestBlock);
        writer.Encode(message.LatestBlockHash);

        writer.EndSequence(sequence);
        byteBuffer.CompleteRlpWrite(in writer, totalLength);
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

    public StatusMessage69 Deserialize(IByteBuffer byteBuffer) =>
        byteBuffer.DeserializeRlp(Deserialize);

    private static StatusMessage69 Deserialize(ref RlpReader reader)
    {
        RlpReader ctx = reader.EnterSequence(out int itemLength);

        StatusMessage69 message = new()
        {
            ProtocolVersion = ctx.DecodeByte(),
            NetworkId = ctx.DecodeULong(),
            GenesisHash = ctx.DecodeKeccak(),
            ForkId = DecodeForkId(ref ctx),
            EarliestBlock = ctx.DecodeULong(),
            LatestBlock = ctx.DecodeULong(),
            LatestBlockHash = ctx.DecodeKeccak()
        };
        reader.ExitSequenceIgnoringRest(ctx, itemLength);
        return message;
    }

    private static void EncodeForkId(ref RlpWriter writer, ForkId forkId)
    {
        int forkIdContentLength = ForkHashLength + Rlp.LengthOf(forkId.Next);
        RlpSequence sequence = writer.StartSequence(forkIdContentLength);
        Span<byte> forkHash = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(forkHash, forkId.ForkHash);
        writer.Encode(forkHash);
        writer.Encode(forkId.Next);
        writer.EndSequence(sequence);
    }

    private static ForkId DecodeForkId(ref RlpReader reader)
    {
        RlpReader ctx = reader.EnterSequence(out int itemLength);
        uint forkHash = (uint)ctx.DecodeUInt256(ForkHashLength - 1);
        ulong next = ctx.DecodeULong();
        reader.ExitSequenceIgnoringRest(ctx, itemLength);
        return new(forkHash, next);
    }

    private static int LengthOfForkId(ForkId forkId)
    {
        int forkIdContentLength = ForkHashLength + Rlp.LengthOf(forkId.Next);
        return Rlp.LengthOfSequence(forkIdContentLength);
    }
}

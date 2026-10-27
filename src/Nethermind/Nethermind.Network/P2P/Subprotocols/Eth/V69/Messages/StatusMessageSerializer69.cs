// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;

public class StatusMessageSerializer69 :
    IZeroInnerMessageSerializer<StatusMessage69>
{
    private const int ForkHashLength = 5;

    public void Serialize(IByteBuffer byteBuffer, StatusMessage69 message) => this.SerializeWithLengths(byteBuffer, message);

    public void Serialize(IByteBuffer byteBuffer, StatusMessage69 message, ref LengthStack lengths)
    {
        lengths.Pop();
        ByteBufferRlpWriter writer = new(byteBuffer);
        writer.StartSequence(lengths.Pop());

        writer.Encode(message.ProtocolVersion);
        writer.Encode(message.NetworkId);
        writer.Encode(message.GenesisHash);
        EncodeForkId(ref writer, message.ForkId);
        writer.Encode(message.EarliestBlock);
        writer.Encode(message.LatestBlock);
        writer.Encode(message.LatestBlockHash);
    }

    public void PopulateLength(StatusMessage69 message, ref LengthStack lengths)
    {
        int contentLength =
            Rlp.LengthOf(message.ProtocolVersion) +
            Rlp.LengthOf(message.NetworkId) +
            Rlp.LengthOf(message.GenesisHash) +
            LengthOfForkId(message.ForkId) +
            Rlp.LengthOf(message.EarliestBlock) +
            Rlp.LengthOf(message.LatestBlock) +
            Rlp.LengthOf(message.LatestBlockHash);

        lengths.Push(contentLength);
        lengths.Push(Rlp.LengthOfSequence(contentLength));
    }

    public StatusMessage69 Deserialize(IByteBuffer byteBuffer) =>
        byteBuffer.DeserializeRlp(Deserialize);

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
        Span<byte> forkHash = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(forkHash, forkId.ForkHash);
        writer.Encode(forkHash);
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

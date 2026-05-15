// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using Nethermind.Core.Buffers;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V71.Messages;

public class BlockAccessListsMessageSerializer : Eth66SerializerBase<BlockAccessListsMessage>
{
    private static readonly RlpLimit RlpLimit = RlpLimit.For<BlockAccessListsMessage>(
        GethSyncLimits.MaxBodyFetch, nameof(BlockAccessListsMessage.BlockAccessLists));

    protected override void SerializeInternal(IByteBuffer byteBuffer, BlockAccessListsMessage message)
        => NettyRlpStream.WriteByteArrayList(byteBuffer, message.BlockAccessLists);

    public override BlockAccessListsMessage Deserialize(IByteBuffer byteBuffer)
    {
        NettyBufferMemoryOwner? memoryOwner = new(byteBuffer);
        Rlp.ValueDecoderContext ctx = new(memoryOwner.Memory, sliceMemory: true);
        int startPosition = ctx.Position;
        RlpByteArrayList? blockAccessLists = null;

        try
        {
            int sequenceLength = ctx.ReadSequenceLength();
            int checkPosition = ctx.Position + sequenceLength;
            long requestId = ctx.DecodeLong();

            blockAccessLists = RlpByteArrayList.DecodeList(ref ctx, memoryOwner);
            Rlp.GuardLimit(blockAccessLists.Count, sequenceLength, RlpLimit);
            ctx.Check(checkPosition);

            memoryOwner = null;
            byteBuffer.SetReaderIndex(byteBuffer.ReaderIndex + ctx.Position - startPosition);
            return new BlockAccessListsMessage(requestId, blockAccessLists);
        }
        catch
        {
            blockAccessLists?.Dispose();
            memoryOwner?.Dispose();
            throw;
        }
    }

    protected override int GetLengthInternal(BlockAccessListsMessage message) =>
        Rlp.LengthOfByteArrayList(message.BlockAccessLists);

    protected override BlockAccessListsMessage DeserializeInternal(ref Rlp.ValueDecoderContext ctx, long requestId) =>
        throw new NotSupportedException($"{nameof(BlockAccessListsMessageSerializer)} requires {nameof(NettyBufferMemoryOwner)} to avoid BAL copies.");
}

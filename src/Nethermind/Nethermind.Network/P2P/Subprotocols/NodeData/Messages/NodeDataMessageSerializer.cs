// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Network.P2P.Subprotocols.NodeData.Messages;

public class NodeDataMessageSerializer : IZeroInnerMessageSerializer<NodeDataMessage>
{
    private static readonly RlpLimit RlpLimit = RlpLimit.For<NodeDataMessage>(NethermindSyncLimits.MaxHashesFetch, nameof(NodeDataMessage.Data));

    public void Serialize(IByteBuffer byteBuffer, NodeDataMessage message)
    {
        if (message.Data is IRlpWrapper rlpList)
        {
            ReadOnlySpan<byte> rlpSpan = rlpList.RlpSpan;
            byteBuffer.EnsureWritable(rlpSpan.Length);
            NettyRlpStream rlpStream = new(byteBuffer);
            rlpStream.Write(rlpSpan);
            return;
        }

        {
            int length = GetLength(message, out int contentLength);
            byteBuffer.EnsureWritable(length);
            RlpStream rlpStream = new NettyRlpStream(byteBuffer);

            rlpStream.StartSequence(contentLength);
            for (int i = 0; i < message.Data.Count; i++)
            {
                rlpStream.Encode(message.Data[i]);
            }
        }
    }

    public NodeDataMessage Deserialize(IByteBuffer byteBuffer)
    {
        NettyBufferMemoryOwner memoryOwner = new(byteBuffer);
        Rlp.ValueDecoderContext ctx = new(memoryOwner.Memory, true);

        int prefixStart = ctx.Position;
        int innerLength = ctx.ReadSequenceLength();
        int totalLength = (ctx.Position - prefixStart) + innerLength;

        RlpByteArrayList list = new(memoryOwner, memoryOwner.Memory.Slice(prefixStart, totalLength));
        byteBuffer.SetReaderIndex(byteBuffer.ReaderIndex + totalLength);

        return new NodeDataMessage(list);
    }

    public int GetLength(NodeDataMessage message, out int contentLength)
    {
        contentLength = 0;
        for (int i = 0; i < message.Data.Count; i++)
        {
            contentLength += Rlp.LengthOf(message.Data[i]);
        }

        return Rlp.LengthOfSequence(contentLength);
    }
}

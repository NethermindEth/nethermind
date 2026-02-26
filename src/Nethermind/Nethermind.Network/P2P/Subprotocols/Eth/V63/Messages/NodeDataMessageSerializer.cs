// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages
{
    public class NodeDataMessageSerializer : IZeroInnerMessageSerializer<NodeDataMessage>
    {
        private static readonly RlpLimit RlpLimit = RlpLimit.For<NodeDataMessage>(NethermindSyncLimits.MaxHashesFetch, nameof(NodeDataMessage.Data));

        public void Serialize(IByteBuffer byteBuffer, NodeDataMessage message)
        {
            NettyRlpStream rlpStream = new(byteBuffer);
            if (rlpStream.TryWriteRlpWrapper(message.Data))
                return;

            int length = GetLength(message, out int contentLength);
            byteBuffer.EnsureWritable(length);

            rlpStream.StartSequence(contentLength);
            for (int i = 0; i < message.Data.Count; i++)
            {
                rlpStream.Encode(message.Data[i]);
            }
        }

        public NodeDataMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyBufferMemoryOwner memoryOwner = new(byteBuffer);
            Rlp.ValueDecoderContext ctx = new(memoryOwner.Memory, true);
            int startPos = ctx.Position;

            RlpByteArrayList list = RlpByteArrayList.DecodeList(ref ctx, memoryOwner);
            byteBuffer.SetReaderIndex(byteBuffer.ReaderIndex + (ctx.Position - startPos));

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
}

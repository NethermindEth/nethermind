// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages
{
    public class NodeDataMessageSerializer : IZeroInnerMessageSerializer<NodeDataMessage>
    {
        private static readonly RlpLimit RlpLimit = RlpLimit.For<NodeDataMessage>(NethermindSyncLimits.MaxHashesFetch, nameof(NodeDataMessage.Data));

        public void Serialize(IByteBuffer byteBuffer, NodeDataMessage message)
        {
            byteBuffer.EnsureWritable(GetLength(message, out _));
            ByteBufferRlpWriter writer = new(byteBuffer);
            writer.WriteByteArrayList(message.Data);
        }

        public NodeDataMessage Deserialize(IByteBuffer byteBuffer) =>
            new(byteBuffer.DecodeRlpByteArrayList());

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

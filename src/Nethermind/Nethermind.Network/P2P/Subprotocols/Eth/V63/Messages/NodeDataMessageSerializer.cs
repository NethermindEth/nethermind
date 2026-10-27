// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages
{
    public class NodeDataMessageSerializer : IZeroInnerMessageSerializer<NodeDataMessage>
    {
        internal static readonly RlpLimit RlpLimit = RlpLimit.For<NodeDataMessage>(NethermindSyncLimits.MaxHashesFetch, nameof(NodeDataMessage.Data));

        public void Serialize(IByteBuffer byteBuffer, NodeDataMessage message) => this.SerializeWithLengths(byteBuffer, message);

        public void Serialize(IByteBuffer byteBuffer, NodeDataMessage message, ref LengthStack lengths)
        {
            lengths.Pop();
            ByteBufferRlpWriter writer = new(byteBuffer);
            writer.WriteByteArrayList(message.Data);
        }

        public NodeDataMessage Deserialize(IByteBuffer byteBuffer) =>
            new(byteBuffer.DecodeRlpByteArrayList(RlpLimit));

        public void PopulateLength(NodeDataMessage message, ref LengthStack lengths)
        {
            int contentLength = 0;
            for (int i = 0; i < message.Data.Count; i++)
            {
                contentLength += Rlp.LengthOf(message.Data[i]);
            }

            lengths.Push(Rlp.LengthOfSequence(contentLength));
        }
    }
}

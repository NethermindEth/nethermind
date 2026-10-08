// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Buffers;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages
{
    public class NodeDataMessageSerializer : IZeroMessageSerializer<NodeDataMessage>
    {
        internal static readonly RlpLimit RlpLimit = RlpLimit.For<NodeDataMessage>(NethermindSyncLimits.MaxHashesFetch, nameof(NodeDataMessage.Data));

        public void Serialize(Span<byte> buffer, NodeDataMessage message)
        {
            RlpWriter writer = new(buffer);
            writer.WriteByteArrayList(message.Data);
        }

        public NodeDataMessage Deserialize(ReadOnlySpan<byte> data, out int consumed)
        {
            PooledBuffer memoryOwner = PooledBuffer.Rent(data.Length);
            data.CopyTo(memoryOwner.Span);
            RlpReader ctx = new(memoryOwner.Span);
            try
            {
                RlpByteArrayList list = RlpByteArrayList.DecodeList(ref ctx, memoryOwner, RlpLimit);
                consumed = ctx.Position;
                return new(list);
            }
            catch
            {
                memoryOwner.Dispose();
                throw;
            }
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

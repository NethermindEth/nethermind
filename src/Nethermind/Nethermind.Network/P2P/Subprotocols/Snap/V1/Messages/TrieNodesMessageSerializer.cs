// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Snap.V1.Messages
{
    public class TrieNodesMessageSerializer : IZeroMessageSerializer<TrieNodesMessage>
    {
        public void Serialize(Span<byte> buffer, TrieNodesMessage message)
        {
            int nodesLength = Rlp.LengthOfByteArrayList(message.Nodes);
            int contentLength = Rlp.LengthOf(message.RequestId) + nodesLength;
            RlpWriter writer = new(buffer);
            writer.StartSequence(contentLength);
            writer.Encode(message.RequestId);
            writer.WriteByteArrayList(message.Nodes);
        }

        public int GetLength(TrieNodesMessage message, out int contentLength)
        {
            contentLength = Rlp.LengthOf(message.RequestId) + Rlp.LengthOfByteArrayList(message.Nodes);
            return Rlp.LengthOfSequence(contentLength);
        }

        public TrieNodesMessage Deserialize(ReadOnlySpan<byte> data, out int consumed)
        {
            PooledBuffer? memoryOwner = PooledBuffer.Rent(data.Length);
            data.CopyTo(memoryOwner.Span);
            RlpReader ctx = new(memoryOwner.Memory.Span);
            RlpByteArrayList? list = null;

            try
            {
                ctx.ReadSequenceLength();
                long requestId = ctx.DecodeLong();

                list = RlpByteArrayList.DecodeList(ref ctx, memoryOwner, SnapMessageLimits.TrieNodesRlpLimit);
                memoryOwner = null;
                consumed = ctx.Position;

                return new TrieNodesMessage(list) { RequestId = requestId };
            }
            catch
            {
                list?.Dispose();
                memoryOwner?.Dispose();
                throw;
            }
        }
    }
}

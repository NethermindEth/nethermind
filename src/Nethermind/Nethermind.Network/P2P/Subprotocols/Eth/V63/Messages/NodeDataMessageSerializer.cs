// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages
{
    public class NodeDataMessageSerializer : IOwningMessageSerializer<NodeDataMessage>
    {
        internal static readonly RlpLimit RlpLimit = RlpLimit.For<NodeDataMessage>(NethermindSyncLimits.MaxHashesFetch, nameof(NodeDataMessage.Data));

        public int Serialize(Span<byte> destination, NodeDataMessage message)
        {
            RlpWriter writer = new(destination);
            writer.WriteByteArrayList(message.Data);
            return writer.Position;
        }

        /// <remarks>Copies <paramref name="data"/>, which the message would otherwise keep referencing.</remarks>
        public NodeDataMessage Deserialize(ReadOnlySpan<byte> data) => Deserialize(OwnedMemory.Copy(data, out Memory<byte> memory), memory);

        public NodeDataMessage Deserialize(IMemoryOwner<byte> owner, Memory<byte> data)
        {
            RlpByteArrayList? list = null;
            try
            {
                RlpReader ctx = new(data.Span);
                list = RlpByteArrayList.DecodeList(ref ctx, owner, data, RlpLimit);
                ctx.CheckEnd();
                return new NodeDataMessage(list);
            }
            catch
            {
                if (list is null) owner.Dispose();
                else list.Dispose();
                throw;
            }
        }

        public int GetLength(NodeDataMessage message)
        {
            int contentLength = 0;
            for (int i = 0; i < message.Data.Count; i++)
            {
                contentLength += Rlp.LengthOf(message.Data[i]);
            }

            return Rlp.LengthOfSequence(contentLength);
        }
    }
}

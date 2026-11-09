// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Snap.V1.Messages
{
    public class TrieNodesMessageSerializer : IOwningMessageSerializer<TrieNodesMessage>
    {
        public int Serialize(Span<byte> destination, TrieNodesMessage message)
        {
            RlpWriter writer = new(destination);
            writer.StartSequence(GetContentLength(message));
            writer.Encode(message.RequestId);
            writer.WriteByteArrayList(message.Nodes);
            return writer.Position;
        }

        /// <remarks>Copies <paramref name="data"/>, which the message would otherwise keep referencing.</remarks>
        public TrieNodesMessage Deserialize(ReadOnlySpan<byte> data) => Deserialize(OwnedMemory.Copy(data, out Memory<byte> memory), memory);

        public TrieNodesMessage Deserialize(IMemoryOwner<byte> owner, Memory<byte> data)
        {
            IMemoryOwner<byte>? memoryOwner = owner;
            RlpByteArrayList? list = null;

            try
            {
                RlpReader ctx = new(data.Span);
                ctx.ReadSequenceLength();
                long requestId = ctx.DecodeLong();

                list = RlpByteArrayList.DecodeList(ref ctx, owner, data, SnapMessageLimits.TrieNodesRlpLimit);
                memoryOwner = null;
                ctx.CheckEnd();

                return new TrieNodesMessage(list) { RequestId = requestId };
            }
            catch
            {
                list?.Dispose();
                memoryOwner?.Dispose();
                throw;
            }
        }

        public int GetLength(TrieNodesMessage message) => Rlp.LengthOfSequence(GetContentLength(message));

        private static int GetContentLength(TrieNodesMessage message) => Rlp.LengthOf(message.RequestId) + Rlp.LengthOfByteArrayList(message.Nodes);
    }
}

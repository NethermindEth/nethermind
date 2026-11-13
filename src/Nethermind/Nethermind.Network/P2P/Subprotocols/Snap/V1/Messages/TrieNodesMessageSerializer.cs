// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Snap.V1.Messages
{
    public class TrieNodesMessageSerializer : IOwningMessageSerializer<TrieNodesMessage>
    {
        public int Serialize(Span<byte> destination, TrieNodesMessage message)
        {
            RlpWriter writer = RlpWriter.ContentWriter(destination);
            RlpSequence sequence = writer.StartSequence(GetContentLength(message));
            writer.Encode(message.RequestId);
            writer.WriteByteArrayList(message.Nodes);
            writer.EndSequence(sequence);
            return writer.Length;
        }

        /// <remarks>Copies <paramref name="data"/>, which the message would otherwise keep referencing.</remarks>
        public TrieNodesMessage Deserialize(ReadOnlySpan<byte> data) => Deserialize(OwnedMemory.Copy(data, out Memory<byte> memory), memory);

        public TrieNodesMessage Deserialize(IMemoryOwner<byte> owner, Memory<byte> data)
        {
            IMemoryOwner<byte>? memoryOwner = owner;
            RlpByteArrayList? list = null;

            try
            {
                RlpReader outer = new(data.Span);
                RlpReader ctx = outer.EnterSequence(out int itemLength);
                long requestId = ctx.DecodeLong();

                list = RlpByteArrayList.DecodeList(ref ctx, owner, data, SnapMessageLimits.TrieNodesRlpLimit);
                memoryOwner = null;
                outer.ExitSequence(ctx, itemLength);
                outer.CheckEnd();

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

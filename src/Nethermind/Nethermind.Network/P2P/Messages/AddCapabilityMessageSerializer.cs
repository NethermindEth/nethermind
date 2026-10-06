// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.Messages
{
    /// <summary>
    /// Serializes P2P capability negotiation messages.
    /// </summary>
    public class AddCapabilityMessageSerializer : IZeroMessageSerializer<AddCapabilityMessage>
    {
        private static readonly RlpLimit RlpLimit = RlpLimit.For<Capability>((int)1.KiB, nameof(Capability.ProtocolCode));

        public void Serialize(Span<byte> buffer, AddCapabilityMessage msg)
        {
            GetLength(msg, out int contentLength);

            RlpWriter writer = new(buffer);
            writer.StartSequence(contentLength);
            writer.Encode(msg.Capability.ProtocolCode.ToLowerInvariant());
            writer.Encode(msg.Capability.Version);
        }

        public AddCapabilityMessage Deserialize(ReadOnlySpan<byte> data, out int consumed)
        {
            RlpReader ctx = new(data);
            AddCapabilityMessage msg = Deserialize(ref ctx);
            consumed = ctx.Position;
            return msg;
        }

        private static AddCapabilityMessage Deserialize(ref RlpReader ctx)
        {
            ctx.ReadSequenceLength();
            string protocolCode = ctx.DecodeString(RlpLimit);
            byte version = ctx.DecodeByte();

            return new AddCapabilityMessage(new Capability(protocolCode, version));
        }
        public int GetLength(AddCapabilityMessage msg, out int contentLength)
        {
            contentLength = Rlp.LengthOf(msg.Capability.ProtocolCode.ToLowerInvariant());
            contentLength += Rlp.LengthOf(msg.Capability.Version);

            return Rlp.LengthOfSequence(contentLength);
        }
    }
}

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
    public class AddCapabilityMessageSerializer : IMessageSerializer<AddCapabilityMessage>
    {
        private static readonly RlpLimit RlpLimit = RlpLimit.For<Capability>((int)1.KiB, nameof(Capability.ProtocolCode));

        public int GetLength(AddCapabilityMessage msg) => Rlp.LengthOfSequence(GetContentLength(msg));

        public int Serialize(Span<byte> destination, AddCapabilityMessage msg)
        {
            RlpWriter writer = new(destination);
            writer.StartSequence(GetContentLength(msg));
            writer.Encode(msg.Capability.ProtocolCode.ToLowerInvariant());
            writer.Encode(msg.Capability.Version);
            return writer.Position;
        }

        public AddCapabilityMessage Deserialize(ReadOnlySpan<byte> data)
        {
            RlpReader ctx = new(data);
            ctx.ReadSequenceLength();
            string protocolCode = ctx.DecodeString(RlpLimit);
            byte version = ctx.DecodeByte();

            return new AddCapabilityMessage(new Capability(protocolCode, version));
        }

        private static int GetContentLength(AddCapabilityMessage msg) =>
            Rlp.LengthOf(msg.Capability.ProtocolCode.ToLowerInvariant())
            + Rlp.LengthOf(msg.Capability.Version);
    }
}

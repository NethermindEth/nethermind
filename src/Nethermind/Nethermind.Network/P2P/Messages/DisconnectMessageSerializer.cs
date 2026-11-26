// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.Messages
{
    public class DisconnectMessageSerializer : IMessageSerializer<DisconnectMessage>
    {
        public int GetLength(DisconnectMessage msg) => Rlp.LengthOfSequence(GetContentLength(msg));

        public int Serialize(Span<byte> destination, DisconnectMessage msg)
        {
            RlpWriter writer = RlpWriter.ContentWriter(destination);

            RlpSequence sequence = writer.StartSequence(GetContentLength(msg));
            writer.Encode((byte)msg.Reason);
            writer.EndSequence(sequence);
            return writer.Length;
        }

        private static int GetContentLength(DisconnectMessage message) => Rlp.LengthOf((byte)message.Reason);

        public DisconnectMessage Deserialize(ReadOnlySpan<byte> msg)
        {
            if (msg.IsEmpty)
            {
                // Sometimes 0x00 was sent, uncompressed, which interpreted as empty buffer by snappy.
                return new DisconnectMessage(EthDisconnectReason.DisconnectRequested);
            }

            if (msg.Length == 1 && msg[0] < Rlp.EmptyByteArrayByte)
            {
                return new DisconnectMessage(msg[0]);
            }

            RlpReader reader = new(msg);
            if (!reader.IsNextSequence)
            {
                ReadOnlySpan<byte> wrapped = reader.DecodeByteArraySpan();
                if (wrapped.IsEmpty) return new DisconnectMessage(EthDisconnectReason.DisconnectRequested);
                reader = new RlpReader(wrapped);
            }

            RlpReader fields = reader.EnterSequence(out _);
            if (!fields.HasNext) return new DisconnectMessage(EthDisconnectReason.DisconnectRequested);
            int reason = fields.DecodeInt();
            DisconnectMessage disconnectMessage = new(reason);
            return disconnectMessage;
        }
    }
}

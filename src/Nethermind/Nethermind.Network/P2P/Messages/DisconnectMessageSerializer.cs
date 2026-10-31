// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.Messages
{
    public class DisconnectMessageSerializer : IMessageSerializer<DisconnectMessage>
    {
        public int GetLength(DisconnectMessage msg) => Rlp.LengthOfSequence(GetContentLength(msg));

        public int Serialize(Span<byte> destination, DisconnectMessage msg)
        {
            RlpWriter writer = new(destination);

            writer.StartSequence(GetContentLength(msg));
            writer.Encode((byte)msg.Reason);
            return writer.Position;
        }

        private static int GetContentLength(DisconnectMessage message) => Rlp.LengthOf((byte)message.Reason);

        public DisconnectMessage Deserialize(ReadOnlySpan<byte> msg)
        {
            if (msg.Length == 1)
            {
                return new DisconnectMessage((EthDisconnectReason)msg[0]);
            }

            if (msg.Length == 0)
            {
                // Sometimes 0x00 was sent, uncompressed, which interpreted as empty buffer by snappy.
                return new DisconnectMessage(EthDisconnectReason.DisconnectRequested);
            }

            RlpReader reader = new(msg);
            if (!reader.IsSequenceNext())
            {
                reader = new RlpReader(reader.DecodeByteArraySpan());
            }

            reader.ReadSequenceLength();
            int reason = reader.DecodeInt();
            DisconnectMessage disconnectMessage = new(reason);
            return disconnectMessage;
        }
    }
}

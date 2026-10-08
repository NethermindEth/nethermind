// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.Messages
{
    public class DisconnectMessageSerializer : IZeroMessageSerializer<DisconnectMessage>
    {
        public void Serialize(Span<byte> buffer, DisconnectMessage msg)
        {
            GetLength(msg, out int contentLength);
            RlpWriter writer = new(buffer);

            writer.StartSequence(contentLength);
            writer.Encode((byte)msg.Reason);
        }

        public int GetLength(DisconnectMessage message, out int contentLength)
        {
            contentLength = Rlp.LengthOf((byte)message.Reason);

            return Rlp.LengthOfSequence(contentLength);
        }


        public DisconnectMessage Deserialize(ReadOnlySpan<byte> data, out int consumed)
        {
            if (data.Length == 1)
            {
                consumed = 1;
                return new DisconnectMessage((EthDisconnectReason)data[0]);
            }

            if (data.Length == 0)
            {
                // Sometimes 0x00 was sent, uncompressed, which interpreted as empty buffer by snappy.
                consumed = 0;
                return new DisconnectMessage(EthDisconnectReason.DisconnectRequested);
            }

            RlpReader reader = new(data);
            if (!reader.IsSequenceNext())
            {
                reader = new RlpReader(reader.DecodeByteArraySpan());
            }

            reader.ReadSequenceLength();
            int reason = reader.DecodeInt();
            consumed = data.Length;
            return new(reason);
        }
    }
}

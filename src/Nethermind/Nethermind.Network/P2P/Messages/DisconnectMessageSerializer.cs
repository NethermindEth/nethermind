// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.Messages
{
    public class DisconnectMessageSerializer : IMessageSerializer<DisconnectMessage>
    {
        public byte[] Serialize(DisconnectMessage msg)
        {
            return Rlp.Encode(
                Rlp.Encode((byte)msg.Reason) // sic!, as a list of 1 element
            ).Bytes;
        }

        public DisconnectMessage Deserialize(byte[] msgBytes)
        {
            if (msgBytes.Length == 1)
            {
                return new DisconnectMessage((DisconnectReason)msgBytes[0]);
            }

            RlpStream rlpStream = msgBytes.AsRlpStream();
            rlpStream.ReadSequenceLength();
            int reason = rlpStream.DecodeInt();
            DisconnectMessage disconnectMessage = new(reason);
            return disconnectMessage;
        }
    }
}

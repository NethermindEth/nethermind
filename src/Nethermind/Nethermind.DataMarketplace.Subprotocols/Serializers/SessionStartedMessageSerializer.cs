// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Subprotocols.Messages;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Subprotocols.Serializers
{
    public class SessionStartedMessageSerializer : IMessageSerializer<SessionStartedMessage>
    {
        public byte[] Serialize(SessionStartedMessage message)
            => Rlp.Encode(Rlp.Encode(message.Session)).Bytes;

        public SessionStartedMessage Deserialize(byte[] bytes)
        {
            var context = bytes.AsRlpStream();
            context.ReadSequenceLength();
            var session = Rlp.Decode<Session>(context);

            return new SessionStartedMessage(session);
        }
    }
}

/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;

namespace Nethermind.Network.P2P
{
    public class DisconnectMessageSerializer : IMessageSerializer<DisconnectMessage>
    {
        public byte[] Serialize(DisconnectMessage message)
        {
            return Rlp.Encode(
                Rlp.Encode((byte)message.Reason) // sic!, as a list of 1 element
            ).Bytes;
        }

        public DisconnectMessage Deserialize(byte[] bytes)
        {
            Rlp.DecoderContext context = bytes.AsRlpContext();
            int reason = context.DecodeInt();
            DisconnectMessage disconnectMessage = new DisconnectMessage(reason);
            return disconnectMessage;
        }
    }
}
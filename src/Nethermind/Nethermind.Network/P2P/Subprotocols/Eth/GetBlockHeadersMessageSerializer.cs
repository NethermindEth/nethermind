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

namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public class GetBlockHeadersMessageSerializer : IMessageSerializer<GetBlockHeadersMessage>
    {
        public byte[] Serialize(GetBlockHeadersMessage message)
        {
            return Rlp.Encode(
                message.StartingBlockHash == null ? Rlp.Encode(message.StartingBlockNumber) : Rlp.Encode(message.StartingBlockHash),
                Rlp.Encode(message.MaxHeaders),
                Rlp.Encode(message.Skip),
                Rlp.Encode(message.Reverse)
            ).Bytes;
        }

        public GetBlockHeadersMessage Deserialize(byte[] bytes)
        {
            GetBlockHeadersMessage message = new GetBlockHeadersMessage();

            DecodedRlp decodedRlp = Rlp.Decode(new Rlp(bytes));
            if (decodedRlp.GetBytes(0).Length == 32)
            {
                message.StartingBlockHash = decodedRlp.GetKeccak(0);
            }
            else
            {
                message.StartingBlockNumber = decodedRlp.GetUnsignedBigInteger(0);
            }

            message.MaxHeaders = decodedRlp.GetInt(1);
            message.Skip = decodedRlp.GetInt(2);
            message.Reverse = decodedRlp.GetInt(3);
            return message;
        }
    }
}
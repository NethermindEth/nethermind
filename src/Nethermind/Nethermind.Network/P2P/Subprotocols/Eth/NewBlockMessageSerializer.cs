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

using Nethermind.Core;
using Nethermind.Core.Encoding;

namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public class NewBlockMessageSerializer : IMessageSerializer<NewBlockMessage>
    {
        public byte[] Serialize(NewBlockMessage message)
        {
            return Rlp.Encode(message.Block, message.TotalDifficulty).Bytes;
        }

        public NewBlockMessage Deserialize(byte[] bytes)
        {
            DecodedRlp decodedRlp = Rlp.Decode(new Rlp(bytes));
            NewBlockMessage message = new NewBlockMessage();
            message.Block = Rlp.Decode<Block>(decodedRlp.GetSequence(0));
            message.Block.Header.RecomputeHash();
            message.TotalDifficulty = decodedRlp.GetUnsignedBigInteger(1);
            return message;
        }
    }
}
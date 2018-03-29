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


using System.Linq;
using System.Numerics;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;

namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public class NewBlockHashesMessageSerializer : IMessageSerializer<NewBlockHashesMessage>
    {
        public byte[] Serialize(NewBlockHashesMessage message, IMessagePad pad = null)
        {
            return Rlp.Encode(
                message.BlockHashes.Select(bh =>
                    Rlp.Encode(
                        Rlp.Encode(bh.Item1),
                        Rlp.Encode(bh.Item2))).ToArray()
            ).Bytes;
        }

        public NewBlockHashesMessage Deserialize(byte[] bytes)
        {
            DecodedRlp decodedRlp = Rlp.Decode(new Rlp(bytes));
            (Keccak, BigInteger)[] blockHashes = new (Keccak, BigInteger)[decodedRlp.Length];
            for (int i = 0; i < decodedRlp.Length; i++)
            {
                DecodedRlp blockHashRlp = decodedRlp.GetSequence(i);
                blockHashes[i] = (blockHashRlp.GetKeccak(0), blockHashRlp.GetUnsignedBigInteger(1));
            }

            return new NewBlockHashesMessage(blockHashes);
        }
    }
}
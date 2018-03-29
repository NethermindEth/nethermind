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
using Nethermind.Core;
using Nethermind.Core.Encoding;

namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public class BlockBodiesMessageSerializer : IMessageSerializer<BlockBodiesMessage>
    {
        public byte[] Serialize(BlockBodiesMessage message)
        {
            return Rlp.Encode(message.Bodies.Select(b => Rlp.Encode(b.Transactions, b.Ommers)).ToArray()).Bytes;
        }

        public BlockBodiesMessage Deserialize(byte[] bytes)
        {
            DecodedRlp decodedRlp = Rlp.Decode(new Rlp(bytes));
            (Transaction[] Transactions, BlockHeader[] Ommers)[] bodies = new (Transaction[] Transactions, BlockHeader[] Ommers)[decodedRlp.Length];
            for (int i = 0; i < bodies.Length; i++)
            {
                DecodedRlp bodyRlp = decodedRlp.GetSequence(i);
                DecodedRlp transactionsRlp = bodyRlp.GetSequence(0);
                DecodedRlp ommersRlp = bodyRlp.GetSequence(1);
                
                Transaction[] transactions = new Transaction[transactionsRlp.Length];
                BlockHeader[] ommers = new BlockHeader[ommersRlp.Length];

                for (int j = 0; j < transactions.Length; j++)
                {
                    transactions[j] = Rlp.Decode<Transaction>(transactionsRlp.GetSequence(j));
                }
                
                for (int j = 0; j < ommers.Length; j++)
                {
                    ommers[j] = Rlp.Decode<BlockHeader>(ommersRlp.GetSequence(j));
                }

                bodies[i].Transactions = transactions;
                bodies[i].Ommers = ommers;
            }
            
            BlockBodiesMessage message = new BlockBodiesMessage();
            message.Bodies = bodies;
            return message;
        }
    }
}
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

using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;

namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public class TransactionsMessageSerializer : IMessageSerializer<TransactionsMessage>, IZeroMessageSerializer<TransactionsMessage>
    {
        public byte[] Serialize(TransactionsMessage message)
        {
            return Rlp.Encode(message.Transactions).Bytes;
        }

        public TransactionsMessage Deserialize(byte[] bytes)
        {
            RlpStream rlpStream = bytes.AsRlpStream();
            return Deserialize(rlpStream);
        }

        private static TransactionsMessage Deserialize(RlpStream rlpStream)
        {
            Transaction[] txs = Rlp.DecodeArray<Transaction>(rlpStream);
            return new TransactionsMessage(txs);
        }

        private TransactionDecoder _decoder = new TransactionDecoder();
        
        public void Serialize(IByteBuffer byteBuffer, TransactionsMessage message)
        {
            NettyRlpStream nettyRlpStream = new NettyRlpStream(byteBuffer);

            int contentLength = 0;
            for (int i = 0; i < message.Transactions.Length; i++)
            {
                contentLength += _decoder.GetLength(message.Transactions[i], RlpBehaviors.None);
            }

            int totalLength = Rlp.LengthOfSequence(contentLength);
            byteBuffer.EnsureWritable(totalLength, true);
            
            nettyRlpStream.StartSequence(contentLength);
            for (int i = 0; i < message.Transactions.Length; i++)
            {
                nettyRlpStream.Encode(message.Transactions[i]);
            }
        }

        public TransactionsMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new NettyRlpStream(byteBuffer);
            return Deserialize(rlpStream);
        }
    }
}
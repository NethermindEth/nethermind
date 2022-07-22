//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Configuration;
using System.Linq;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class BlockBodiesMessageSerializer : IZeroInnerMessageSerializer<BlockBodiesMessage>
    {
        private readonly TxDecoder _txDecoder = new();
        private readonly HeaderDecoder _headerDecoder = new();

        public void Serialize(IByteBuffer byteBuffer, BlockBodiesMessage message)
        {

            int totalLength = GetLength(message, out int contentLength);
            byteBuffer.EnsureWritable(totalLength, true);
            NettyRlpStream stream = new(byteBuffer);
            stream.StartSequence(contentLength);
            foreach (BlockBody? body in message.Bodies)
            {
                if (body == null)
                {
                    stream.Encode(Rlp.OfEmptySequence);
                }
                else
                {
                    SerializeBody(stream, body);
                }
            }
        }

        private void SerializeBody(NettyRlpStream stream, BlockBody body)
        {
            stream.StartSequence(GetBodyLength(body));
            stream.StartSequence(GetTxLength(body.Transactions));
            foreach (Transaction? txn in body.Transactions)
            {
                stream.Encode(txn);
            }

            stream.StartSequence(GetUnclesLength(body.Uncles));
            foreach (var uncle in body.Uncles)
            {
                stream.Encode(uncle);
            }
        }

        public BlockBodiesMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new(byteBuffer);
            return Deserialize(rlpStream);
        }

        public int GetLength(BlockBodiesMessage message, out int contentLength)
        {
            contentLength = message.Bodies.Select(b => b == null
                ? Rlp.OfEmptySequence.Length
                : Rlp.LengthOfSequence(
                    GetBodyLength(b)
                    )
            ).Sum();
            return Rlp.LengthOfSequence(contentLength);
        }

        private int GetBodyLength(BlockBody b)
        {
            return Rlp.LengthOfSequence(GetTxLength(b.Transactions)) +
                Rlp.LengthOfSequence(GetUnclesLength(b.Uncles));
        }

        private int GetTxLength(Transaction[] transactions)
        {
            int txLength = 0;
            for (int i = 0; i < transactions.Length; i++)
            {
                txLength += _txDecoder.GetLength(transactions[i], RlpBehaviors.None);
            }

            return txLength;
        }

        private int GetUnclesLength(BlockHeader[] headers)
        {
            int unclesLength = 0;
            for (int i = 0; i < headers.Length; i++)
            {
                unclesLength += _headerDecoder.GetLength(headers[i], RlpBehaviors.None);
            }

            return unclesLength;
        }

        public static BlockBodiesMessage Deserialize(RlpStream rlpStream)
        {
            BlockBodiesMessage message = new();
            message.Bodies = rlpStream.DecodeArray(ctx =>
            {
                int sequenceLength = rlpStream.ReadSequenceLength();
                if (sequenceLength == 0)
                {
                    return null;
                }

                // quite significant allocations (>0.5%) here based on a sample 3M blocks sync
                // (just on these delegates)
                Transaction[] transactions = rlpStream.DecodeArray(_ => Rlp.Decode<Transaction>(ctx));
                BlockHeader[] uncles = rlpStream.DecodeArray(_ => Rlp.Decode<BlockHeader>(ctx));
                return new BlockBody(transactions, uncles);
            }, false);

            return message;
        }
    }
}

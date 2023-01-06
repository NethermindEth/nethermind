// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
                int startingPosition = rlpStream.Position;
                if (sequenceLength == 0)
                {
                    return null;
                }

                // quite significant allocations (>0.5%) here based on a sample 3M blocks sync
                // (just on these delegates)
                Transaction[] transactions = rlpStream.DecodeArray(_ => Rlp.Decode<Transaction>(ctx));
                BlockHeader[] uncles = rlpStream.DecodeArray(_ => Rlp.Decode<BlockHeader>(ctx));
                Withdrawal[]? withdrawals = null;
                if (rlpStream.ReadNumberOfItemsRemaining(startingPosition + sequenceLength, 1) > 0)
                {
                    withdrawals = rlpStream.DecodeArray(_ => Rlp.Decode<Withdrawal>(ctx));
                }

                return new BlockBody(transactions, uncles, withdrawals);
            }, false);

            return message;
        }
    }
}

// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class BlockBodiesMessageSerializer : IZeroInnerMessageSerializer<BlockBodiesMessage>
    {
        private readonly TxDecoder _txDecoder = new TxDecoder();
        private readonly HeaderDecoder _headerDecoder = new HeaderDecoder();
        private readonly WithdrawalDecoder _withdrawalDecoderDecoder = new WithdrawalDecoder();

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
            foreach (BlockHeader? uncle in body.Uncles)
            {
                stream.Encode(uncle);
            }

            if (body.Withdrawals != null)
            {
                stream.StartSequence(GetWithdrawalsLength(body.Withdrawals));
                foreach (Withdrawal? withdrawal in body.Withdrawals)
                {
                    stream.Encode(withdrawal);
                }
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
                : Rlp.LengthOfSequence(GetBodyLength(b))
            ).Sum();
            return Rlp.LengthOfSequence(contentLength);
        }

        private int GetBodyLength(BlockBody b)
        {
            if (b.Withdrawals != null)
            {
                return Rlp.LengthOfSequence(GetTxLength(b.Transactions)) +
                       Rlp.LengthOfSequence(GetUnclesLength(b.Uncles)) + Rlp.LengthOfSequence(GetWithdrawalsLength(b.Withdrawals));
            }
            return Rlp.LengthOfSequence(GetTxLength(b.Transactions)) +
                Rlp.LengthOfSequence(GetUnclesLength(b.Uncles));
        }

        private int GetTxLength(Transaction[] transactions)
        {

            return transactions.Sum(t => _txDecoder.GetLength(t, RlpBehaviors.None));
        }

        private int GetUnclesLength(BlockHeader[] headers)
        {

            return headers.Sum(t => _headerDecoder.GetLength(t, RlpBehaviors.None));
        }

        private int GetWithdrawalsLength(Withdrawal[] withdrawals)
        {

            return withdrawals.Sum(t => _withdrawalDecoderDecoder.GetLength(t, RlpBehaviors.None));
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
                if (rlpStream.PeekNumberOfItemsRemaining(startingPosition + sequenceLength, 1) > 0)
                {
                    withdrawals = rlpStream.DecodeArray(_ => Rlp.Decode<Withdrawal>(ctx));
                }

                return new BlockBody(transactions, uncles, withdrawals);
            }, false);

            return message;
        }
    }
}

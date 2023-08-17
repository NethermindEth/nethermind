// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class BlockBodiesMessageSerializer : IZeroInnerMessageSerializer<BlockBodiesMessage>
    {
        private readonly BlockBodyDecoder _blockBodyDecoder = new BlockBodyDecoder();

        public void Serialize(IByteBuffer byteBuffer, BlockBodiesMessage message)
        {
            int totalLength = GetLength(message, out int contentLength);
            byteBuffer.EnsureWritable(totalLength, true);
            NettyRlpStream stream = new(byteBuffer);
            stream.StartSequence(contentLength);
            foreach (BlockBody? body in message.Bodies.Bodies)
            {
                if (body == null)
                {
                    stream.Encode(Rlp.OfEmptySequence);
                }
                else
                {
                    _blockBodyDecoder.Serialize(stream, body);
                }
            }
        }

        public int GetLength(BlockBodiesMessage message, out int contentLength)
        {
            contentLength = message.Bodies.Bodies.Select(b => b == null
                ? Rlp.OfEmptySequence.Length
                : Rlp.LengthOfSequence(_blockBodyDecoder.GetBodyLength(b))
            ).Sum();
            return Rlp.LengthOfSequence(contentLength);
        }

        public BlockBodiesMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyBufferMemoryOwner memoryOwner = new(byteBuffer);

            Rlp.ValueDecoderContext ctx = new(memoryOwner.Memory, true);
            int startingPosition = ctx.Position;
            BlockBody[]? bodies = ctx.DecodeArray(_blockBodyDecoder, false);
            byteBuffer.SetReaderIndex(byteBuffer.ReaderIndex + (ctx.Position - startingPosition));

            return new() { Bodies = new(bodies, memoryOwner) };
        }

        private class BlockBodyDecoder : IRlpValueDecoder<BlockBody>
        {
            private readonly TxDecoder _txDecoder = new();
            private readonly HeaderDecoder _headerDecoder = new();
            private readonly WithdrawalDecoder _withdrawalDecoderDecoder = new();

            public int GetLength(BlockBody item, RlpBehaviors rlpBehaviors)
            {
                return Rlp.LengthOfSequence(GetBodyLength(item));
            }

            public int GetBodyLength(BlockBody b)
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

            public BlockBody? Decode(ref Rlp.ValueDecoderContext ctx, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
            {
                int sequenceLength = ctx.ReadSequenceLength();
                int startingPosition = ctx.Position;
                if (sequenceLength == 0)
                {
                    return null;
                }

                // quite significant allocations (>0.5%) here based on a sample 3M blocks sync
                // (just on these delegates)
                Transaction[] transactions = ctx.DecodeArray(_txDecoder);
                BlockHeader[] uncles = ctx.DecodeArray(_headerDecoder);
                Withdrawal[]? withdrawals = null;
                if (ctx.PeekNumberOfItemsRemaining(startingPosition + sequenceLength, 1) > 0)
                {
                    withdrawals = ctx.DecodeArray(_withdrawalDecoderDecoder);
                }

                return new BlockBody(transactions, uncles, withdrawals);
            }

            public void Serialize(RlpStream stream, BlockBody body)
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
        }
    }
}

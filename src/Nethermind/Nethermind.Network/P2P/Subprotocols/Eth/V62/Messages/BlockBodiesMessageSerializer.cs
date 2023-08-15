// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
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
            foreach (BlockBody? body in message.Bodies.DeserializeBodies())
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
            stream.StartSequence(_blockBodyDecoder.GetBodyLength(body));
            stream.StartSequence(_blockBodyDecoder.GetTxLength(body.Transactions));
            foreach (Transaction? txn in body.Transactions)
            {
                stream.Encode(txn);
            }

            stream.StartSequence(_blockBodyDecoder.GetUnclesLength(body.Uncles));
            foreach (BlockHeader? uncle in body.Uncles)
            {
                stream.Encode(uncle);
            }

            if (body.Withdrawals != null)
            {
                stream.StartSequence(_blockBodyDecoder.GetWithdrawalsLength(body.Withdrawals));
                foreach (Withdrawal? withdrawal in body.Withdrawals)
                {
                    stream.Encode(withdrawal);
                }
            }
        }

        public BlockBodiesMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyBufferMemoryOwner memoryOwner = new(byteBuffer);
            BlockBodiesMessage msg = Deserialize(memoryOwner.Memory, memoryOwner);
            byteBuffer.SetReaderIndex(byteBuffer.ReaderIndex + byteBuffer.ReadableBytes);
            return msg;
        }

        public int GetLength(BlockBodiesMessage message, out int contentLength)
        {
            contentLength = message.Bodies.DeserializeBodies().Select(b => b == null
                ? Rlp.OfEmptySequence.Length
                : Rlp.LengthOfSequence(_blockBodyDecoder.GetBodyLength(b))
            ).Sum();
            return Rlp.LengthOfSequence(contentLength);
        }

        private BlockBodiesMessage Deserialize(Memory<byte> memory, IMemoryOwner<byte> memoryOwner)
        {
            Rlp.ValueDecoderContext ctx = new Rlp.ValueDecoderContext(memory, true);
            BlockBodiesMessage message = new();
            message.Bodies = new(ctx.DecodeArray(_blockBodyDecoder, false), memoryOwner);

            return message;
        }

        private class BlockBodyDecoder : IRlpValueDecoder<BlockBody>
        {
            private readonly TxDecoder _txDecoder = new TxDecoder();
            private readonly HeaderDecoder _headerDecoder = new HeaderDecoder();
            private readonly WithdrawalDecoder _withdrawalDecoderDecoder = new WithdrawalDecoder();

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

            public int GetTxLength(Transaction[] transactions)
            {

                return transactions.Sum(t => _txDecoder.GetLength(t, RlpBehaviors.None));
            }

            public int GetUnclesLength(BlockHeader[] headers)
            {

                return headers.Sum(t => _headerDecoder.GetLength(t, RlpBehaviors.None));
            }

            public int GetWithdrawalsLength(Withdrawal[] withdrawals)
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
        }
    }
}

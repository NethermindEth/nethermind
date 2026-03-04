// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Serialization.Rlp
{
    public sealed class BlockDecoder(IHeaderDecoder headerDecoder) : RlpValueDecoder<Block>
    {
        private readonly IHeaderDecoder _headerDecoder = headerDecoder ?? throw new ArgumentNullException(nameof(headerDecoder));
        private readonly BlockBodyDecoder _blockBodyDecoder = new BlockBodyDecoder(headerDecoder);

        [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(BlockDecoder))]
        public BlockDecoder() : this(new HeaderDecoder()) { }

        private (int Total, int Txs, int Uncles, int? Withdrawals) GetContentLength(Block item, RlpBehaviors rlpBehaviors)
        {
            int headerLength = _headerDecoder.GetLength(item.Header, rlpBehaviors);

            (int txs, int uncles, int? withdrawals) = _blockBodyDecoder.GetBodyComponentLength(item.Body);

            byte[][]? encodedTxs = item.EncodedTransactions;
            if (encodedTxs is not null)
            {
                txs = GetPreEncodedTxLength(item.Transactions, encodedTxs);
            }

            int contentLength =
                headerLength +
                Rlp.LengthOfSequence(txs) +
                Rlp.LengthOfSequence(uncles) +
                (withdrawals is not null ? Rlp.LengthOfSequence(withdrawals.Value) : 0);
            return (contentLength, txs, uncles, withdrawals);
        }

        private static int GetPreEncodedTxLength(Transaction[] txs, byte[][] encodedTxs)
        {
            int sum = 0;
            for (int i = 0; i < encodedTxs.Length; i++)
            {
                sum += TxDecoder.GetWrappedTxLength(txs[i].Type, encodedTxs[i].Length);
            }
            return sum;
        }

        public override int GetLength(Block? item, RlpBehaviors rlpBehaviors)
        {
            if (item is null)
            {
                return Rlp.OfEmptyList.Length;
            }

            return Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors).Total);
        }

        protected override Block? DecodeInternal(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (decoderContext.IsNextItemEmptyList())
            {
                decoderContext.ReadByte();
                return null;
            }

            int sequenceLength = decoderContext.ReadSequenceLength();
            int blockCheck = decoderContext.Position + sequenceLength;

            BlockHeader header = _headerDecoder.Decode(ref decoderContext);
            BlockBody body = _blockBodyDecoder.DecodeUnwrapped(ref decoderContext, blockCheck);

            Block block = new(header, body)
            {
                EncodedSize = Rlp.LengthOfSequence(sequenceLength)
            };

            return block;
        }

        public Rlp Encode(Block? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                return Rlp.OfEmptyList;
            }

            RlpStream rlpStream = new(GetLength(item, rlpBehaviors));
            Encode(rlpStream, item, rlpBehaviors);
            return new(rlpStream.Data.ToArray());
        }

        public override void Encode(RlpStream stream, Block? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                stream.EncodeNullObject();
                return;
            }

            (int contentLength, int txsLength, int unclesLength, int? withdrawalsLength) = GetContentLength(item, rlpBehaviors);
            stream.StartSequence(contentLength);
            _headerDecoder.Encode(stream, item.Header);
            stream.StartSequence(txsLength);

            byte[][]? encodedTxs = item.EncodedTransactions;
            if (encodedTxs is not null)
            {
                for (int i = 0; i < encodedTxs.Length; i++)
                {
                    TxDecoder.WriteWrappedFormat(stream, item.Transactions[i].Type, encodedTxs[i]);
                }
            }
            else
            {
                for (int i = 0; i < item.Transactions.Length; i++)
                {
                    stream.Encode(item.Transactions[i]);
                }
            }

            stream.StartSequence(unclesLength);
            for (int i = 0; i < item.Uncles.Length; i++)
            {
                stream.Encode(item.Uncles[i]);
            }

            if (withdrawalsLength.HasValue)
            {
                stream.StartSequence(withdrawalsLength.Value);

                for (int i = 0; i < item.Withdrawals.Length; i++)
                {
                    stream.Encode(item.Withdrawals[i]);
                }
            }
        }

        public ReceiptRecoveryBlock? DecodeToReceiptRecoveryBlock(MemoryManager<byte>? memoryManager, Memory<byte> memory, RlpBehaviors rlpBehaviors)
        {
            Rlp.ValueDecoderContext decoderContext = new Rlp.ValueDecoderContext(memory, true);

            if (decoderContext.IsNextItemEmptyList())
            {
                decoderContext.ReadByte();
                return null;
            }

            int sequenceLength = decoderContext.ReadSequenceLength();
            int blockCheck = decoderContext.Position + sequenceLength;

            BlockHeader header = _headerDecoder.Decode(ref decoderContext);

            int contentLength = decoderContext.ReadSequenceLength();
            int transactionCount = decoderContext.PeekNumberOfItemsRemaining(decoderContext.Position + contentLength);

            Memory<byte> transactionMemory = decoderContext.ReadMemory(contentLength);

            decoderContext.SkipItem(); // Skip uncles

            if (decoderContext.Position != blockCheck)
            {
                decoderContext.SkipItem(); // Skip withdrawals
            }
            if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
            {
                decoderContext.Check(blockCheck);
            }

            return new ReceiptRecoveryBlock(memoryManager, header, transactionMemory, transactionCount);
        }
    }
}

// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Nethermind.Core;
using Nethermind.Serialization.Rlp.Eip7928;

namespace Nethermind.Serialization.Rlp
{
    public sealed class BlockDecoder(IHeaderDecoder headerDecoder) : RlpValueDecoder<Block>
    {
        private readonly IHeaderDecoder _headerDecoder = headerDecoder ?? throw new ArgumentNullException(nameof(headerDecoder));
        private readonly BlockBodyDecoder _blockBodyDecoder = new(headerDecoder);
        private readonly BlockAccessListDecoder _blockAccessListDecoder = new();

        public BlockDecoder() : this(new HeaderDecoder()) { }

        protected override Block? DecodeInternal(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (rlpStream.Length == 0)
            {
                throw new RlpException($"Received a 0 length stream when decoding a {nameof(Block)}");
            }

            // Console.WriteLine("DECODING BLOCK");
            // Console.WriteLine(Convert.ToHexString(rlpStream.Data));

            if (rlpStream.IsNextItemNull())
            {
                rlpStream.ReadByte();
                return null;
            }

            Span<byte> contentSpan = rlpStream.PeekNextItem();
            Rlp.ValueDecoderContext ctx = new Rlp.ValueDecoderContext(contentSpan);
            Block? decoded = Decode(ref ctx, rlpBehaviors);
            rlpStream.Position += contentSpan.Length;
            return decoded;
        }

        private (int Total, int Txs, int Uncles, int? Withdrawals, int? BlockAccessList, int? GeneratedBlockAccessList) GetContentLength(Block item, RlpBehaviors rlpBehaviors)
        {
            int headerLength = _headerDecoder.GetLength(item.Header, rlpBehaviors);

            (int txs, int uncles, int? withdrawals, int? blockAccessList) = _blockBodyDecoder.GetBodyComponentLength(item.Body);
            int? generatedBlockAccessList = item.GeneratedBlockAccessList is null ? null : _blockAccessListDecoder.GetLength(item.GeneratedBlockAccessList.Value, rlpBehaviors);
            int contentLength =
                headerLength +
                Rlp.LengthOfSequence(txs) +
                Rlp.LengthOfSequence(uncles) +
                (withdrawals is not null ? Rlp.LengthOfSequence(withdrawals.Value) : 0) +
                (blockAccessList is not null ? blockAccessList.Value : 0) +
                (generatedBlockAccessList is not null ? generatedBlockAccessList.Value : 0);
            return (contentLength, txs, uncles, withdrawals, blockAccessList, generatedBlockAccessList);
        }

        public override int GetLength(Block? item, RlpBehaviors rlpBehaviors)
        {
            if (item is null)
            {
                return 1;
            }

            return Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors).Total);
        }

        protected override Block? DecodeInternal(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (decoderContext.IsNextItemNull())
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
                EncodedSize = Rlp.LengthOfSequence(sequenceLength),
                GeneratedBlockAccessList = decoderContext.PeekNumberOfItemsRemaining() == 0 ? null : _blockAccessListDecoder.Decode(ref decoderContext, rlpBehaviors),
                EncodedBlockAccessList = body.BlockAccessList is null ? null : Rlp.Encode(body.BlockAccessList.Value).Bytes // todo: possible without reencoding?
            };

            return block;
        }

        public Rlp Encode(Block? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                return Rlp.OfEmptySequence;
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

            (int contentLength, int txsLength, int unclesLength, int? withdrawalsLength, int? balLength, int? genBalLength) = GetContentLength(item, rlpBehaviors);
            stream.StartSequence(contentLength);
            _headerDecoder.Encode(stream, item.Header);
            stream.StartSequence(txsLength);
            for (int i = 0; i < item.Transactions.Length; i++)
            {
                stream.Encode(item.Transactions[i]);
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

            if (item.BlockAccessList is not null)
            {
                // stream.StartSequence(balLength.Value);
                stream.Encode(item.BlockAccessList.Value);
            }

            if (item.GeneratedBlockAccessList is not null)
            {
                // stream.StartSequence(genBalLength.Value);
                stream.Encode(item.GeneratedBlockAccessList.Value);
            }
        }

        public ReceiptRecoveryBlock? DecodeToReceiptRecoveryBlock(MemoryManager<byte>? memoryManager, Memory<byte> memory, RlpBehaviors rlpBehaviors)
        {
            Rlp.ValueDecoderContext decoderContext = new Rlp.ValueDecoderContext(memory, true);

            if (decoderContext.IsNextItemNull())
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

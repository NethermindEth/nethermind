// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Serialization.Rlp
{
    public sealed class BlockDecoder(IHeaderDecoder headerDecoder) : RlpDecoder<Block>
    {
        private readonly IHeaderDecoder _headerDecoder = headerDecoder ?? throw new ArgumentNullException(nameof(headerDecoder));
        private readonly BlockBodyDecoder _blockBodyDecoder = new(headerDecoder);
        private readonly WithdrawalDecoder _withdrawalDecoder = new();

        [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(BlockDecoder))]
        public BlockDecoder() : this(new HeaderDecoder()) { }

        public override void PopulateLength(Block? item, in LengthListWriter lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                lengths.Add(Rlp.OfEmptyList.Length);
                return;
            }

            using LengthListWriter sequence = lengths.BeginContainer();
            _headerDecoder.PopulateLength(item.Header, sequence, rlpBehaviors);
            if (item.EncodedTransactions is { } encodedTxs)
            {
                using LengthListWriter txs = sequence.BeginContainer();
                txs.Add(GetPreEncodedTxLength(item.Transactions, encodedTxs));
            }
            else
            {
                TxDecoder.Instance.PopulateSequenceLength(item.Transactions, sequence);
            }

            _headerDecoder.PopulateSequenceLength(item.Uncles, sequence);
            if (item.Withdrawals is { } withdrawals)
            {
                _withdrawalDecoder.PopulateSequenceLength(withdrawals, sequence);
            }
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

        protected override Block? DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (decoderContext.TryConsumeNull(out LiteRlpReader rlp, out int position)) return null;

            rlp.ReadSequenceLength(ref position, out int sequenceLength);
            int blockCheck = position + sequenceLength;
            decoderContext.Position = position;

            BlockHeader header = _headerDecoder.DecodeGuardNotNull(ref decoderContext);
            // Blocks retain transactions without the return path provided by OwnedBlockBodies.Dispose.
            BlockBody body = _blockBodyDecoder.DecodeUnwrapped(ref decoderContext, blockCheck, usePooledTransactions: false);

            Block block = new(header, body)
            {
                EncodedSize = Rlp.LengthOfSequence(sequenceLength)
            };

            return block;
        }

        public ArrayPoolSpan<byte> EncodeToArrayPoolSpan(Block block, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            LengthList lengths = new(stackalloc int[LengthList.MaxStackallocLength]);
            try
            {
                ArrayPoolSpan<byte> buffer = new(this.PopulateLength(block, ref lengths, rlpBehaviors));
                try
                {
                    RlpEncoderExtensions.EncodeTo(this, block, ref lengths, buffer, rlpBehaviors);
                    return buffer;
                }
                catch
                {
                    buffer.Dispose();
                    throw;
                }
            }
            finally
            {
                lengths.Dispose();
            }
        }

        public override void Encode<TWriter>(ref TWriter writer, Block? item, ref LengthList lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                writer.EncodeNullObject();
                return;
            }

            writer.StartSequence(lengths.Read());
            _headerDecoder.Encode(ref writer, item.Header, ref lengths, rlpBehaviors);

            if (item.EncodedTransactions is { } encodedTxs)
            {
                writer.StartSequence(lengths.Read());
                for (int i = 0; i < encodedTxs.Length; i++)
                {
                    TxDecoder.WriteWrappedFormat(ref writer, item.Transactions[i].Type, encodedTxs[i]);
                }
            }
            else
            {
                TxDecoder.Instance.EncodeSequence(ref writer, item.Transactions, ref lengths);
            }

            _headerDecoder.EncodeSequence(ref writer, item.Uncles, ref lengths);
            if (item.Withdrawals is { } withdrawals)
            {
                _withdrawalDecoder.EncodeSequence(ref writer, withdrawals, ref lengths);
            }
        }

        public ReceiptRecoveryBlock? DecodeToReceiptRecoveryBlock(MemoryManager<byte>? memoryManager, Memory<byte> memory, RlpBehaviors rlpBehaviors)
        {
            RlpReader decoderContext = new(memory.Span);

            if (decoderContext.IsNextItemEmptyList())
            {
                decoderContext.ReadByte();
                return null;
            }

            int sequenceLength = decoderContext.ReadSequenceLength();
            int blockCheck = decoderContext.Position + sequenceLength;

            BlockHeader header = _headerDecoder.DecodeGuardNotNull(ref decoderContext);

            int contentLength = decoderContext.ReadSequenceLength();
            int transactionCount = decoderContext.PeekNumberOfItemsRemaining(decoderContext.Position + contentLength);

            Memory<byte> transactionMemory = memory.Slice(decoderContext.Position, contentLength);
            decoderContext.SkipBytes(contentLength);

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

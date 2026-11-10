// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Serialization.FluentRlp;
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

        private (int Total, int Txs, int Uncles, int? Withdrawals) GetContentLength(Block item, RlpBehaviors rlpBehaviors)
        {
            int headerLength = _headerDecoder.GetLength(item.Header, rlpBehaviors);

            byte[][]? encodedTxs = item.EncodedTransactions;
            (int txs, int uncles, int? withdrawals) = encodedTxs is null
                ? _blockBodyDecoder.GetBodyComponentLength(item.Body)
                : _blockBodyDecoder.GetBodyComponentLength(item.Body, GetPreEncodedTxLength(item.Transactions, encodedTxs));

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

        protected override Block? DecodeInternal(ref RlpReader reader, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
            => (rlpBehaviors & RlpBehaviors.TrustedInput) != 0 ? Decode<OffFlag>(ref reader, rlpBehaviors) : Decode<OnFlag>(ref reader, rlpBehaviors);

        private Block? Decode<TCanonicalChecks>(ref RlpReader reader, RlpBehaviors rlpBehaviors) where TCanonicalChecks : struct, IFlag
        {
            if (reader.TryReadEmptySequence()) return null;

            RlpReader fields = reader.EnterSequence<TCanonicalChecks>(out int itemLength);
            // The nested items get only the input mode, so that the other behaviors apply to this item only.
            BlockHeader header = _headerDecoder.DecodeGuardNotNull(ref fields, rlpBehaviors & RlpBehaviors.TrustedInput);
            // Blocks retain transactions without the return path provided by OwnedBlockBodies.Dispose.
            BlockBody body = _blockBodyDecoder.DecodeUnwrapped<TCanonicalChecks>(ref fields, rlpBehaviors, usePooledTransactions: false);
            reader.ExitSequence(in fields, itemLength);

            return new Block(header, body)
            {
                EncodedSize = itemLength
            };
        }

        public override Rlp Encode(Block? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                return Rlp.OfEmptyList;
            }

            (int Total, int Txs, int Uncles, int? Withdrawals) lengths = GetContentLength(item, rlpBehaviors);
            byte[] bytes = GC.AllocateUninitializedArray<byte>(Rlp.LengthOfSequence(lengths.Total));
            RlpWriter writer = RlpWriter.ContentWriter(bytes);
            EncodeContent(ref writer, item, lengths, rlpBehaviors);
            Rlp.CheckLength(in writer, bytes.Length);
            return new(bytes);
        }

        public ArrayPoolSpan<byte> EncodeToArrayPoolSpan(Block block, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            (int Total, int Txs, int Uncles, int? Withdrawals) lengths = GetContentLength(block, rlpBehaviors);
            ArrayPoolSpan<byte> buffer = new(Rlp.LengthOfSequence(lengths.Total));
            try
            {
                RlpWriter writer = RlpWriter.ContentWriter(buffer);
                EncodeContent(ref writer, block, lengths, rlpBehaviors);
                Rlp.CheckLength(in writer, buffer.Length);
                return buffer;
            }
            catch
            {
                buffer.Dispose();
                throw;
            }
        }

        public override void Encode(ref RlpWriter writer, Block? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                writer.EncodeNullObject();
                return;
            }

            EncodeContent(ref writer, item, GetContentLength(item, rlpBehaviors), rlpBehaviors);
        }

        private void EncodeContent(ref RlpWriter writer, Block item, (int Total, int Txs, int Uncles, int? Withdrawals) lengths, RlpBehaviors rlpBehaviors)
        {
            (int contentLength, int txsLength, int unclesLength, int? withdrawalsLength) = lengths;
            RlpSequence sequence = writer.StartSequence(contentLength);
            // The header length came from the same behaviors, so the header is written with them too.
            _headerDecoder.Encode(ref writer, item.Header, rlpBehaviors);
            RlpSequence txs = writer.StartSequence(txsLength);

            byte[][]? encodedTxs = item.EncodedTransactions;
            if (encodedTxs is not null)
            {
                for (int i = 0; i < encodedTxs.Length; i++)
                {
                    TxDecoder.WriteWrappedFormat(ref writer, item.Transactions[i].Type, encodedTxs[i]);
                }
            }
            else
            {
                for (int i = 0; i < item.Transactions.Length; i++)
                {
                    TxDecoder.Instance.Encode(ref writer, item.Transactions[i]);
                }
            }

            writer.EndSequence(txs);
            RlpSequence uncles = writer.StartSequence(unclesLength);
            for (int i = 0; i < item.Uncles.Length; i++)
            {
                _headerDecoder.Encode(ref writer, item.Uncles[i]);
            }

            writer.EndSequence(uncles);
            if (item.Withdrawals is { } withdrawals)
            {
                RlpSequence withdrawalsSequence = writer.StartSequence(withdrawalsLength
                    ?? throw new InvalidOperationException("Withdrawal payload length is missing."));

                for (int i = 0; i < withdrawals.Length; i++)
                {
                    _withdrawalDecoder.Encode(ref writer, withdrawals[i]);
                }

                writer.EndSequence(withdrawalsSequence);
            }

            writer.EndSequence(sequence);
        }

        public ReceiptRecoveryBlock? DecodeToReceiptRecoveryBlock(MemoryManager<byte>? memoryManager, Memory<byte> memory, RlpBehaviors rlpBehaviors)
            => (rlpBehaviors & RlpBehaviors.TrustedInput) != 0
                ? DecodeToReceiptRecoveryBlock<OffFlag>(memoryManager, memory, rlpBehaviors)
                : DecodeToReceiptRecoveryBlock<OnFlag>(memoryManager, memory, rlpBehaviors);

        private ReceiptRecoveryBlock? DecodeToReceiptRecoveryBlock<TCanonicalChecks>(MemoryManager<byte>? memoryManager, Memory<byte> memory, RlpBehaviors rlpBehaviors)
            where TCanonicalChecks : struct, IFlag
        {
            RlpReader reader = new(memory.Span);
            if (reader.TryReadEmptySequence()) return null;

            RlpReader fields = reader.EnterSequence<TCanonicalChecks>(out _);
            BlockHeader header = _headerDecoder.DecodeGuardNotNull(ref fields, rlpBehaviors & RlpBehaviors.TrustedInput);

            RlpReader transactions = fields.EnterSequence<TCanonicalChecks>(out int transactionsLength);
            int transactionCount = transactions.CountItems<TCanonicalChecks>(int.MaxValue);
            Memory<byte> transactionMemory = memory.SliceOf(transactions.ReadRaw(transactions.Remaining));
            fields.ExitSequence(in transactions, transactionsLength);

            fields.SkipItem<TCanonicalChecks>(); // Skip uncles

            if (fields.HasNext)
            {
                fields.SkipItem<TCanonicalChecks>(); // Skip withdrawals
            }
            if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
            {
                fields.CheckEnd();
            }

            return new ReceiptRecoveryBlock(memoryManager, header, transactionMemory, transactionCount, rlpBehaviors & RlpBehaviors.TrustedInput);
        }
    }
}

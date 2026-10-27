// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Stats.SyncLimits;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages
{
    public class ReceiptsMessageSerializer : IZeroInnerMessageSerializer<ReceiptsMessage>
    {
        private const ulong NoBlockSeenYet = ulong.MaxValue;

        private static readonly RlpLimit ReceiptsRlpLimit = RlpLimit.For<ReceiptsMessage>(NethermindSyncLimits.MaxHashesFetch, nameof(ReceiptsMessage.TxReceipts));
        // A block has one receipt per transaction, so it is bounded like its transaction list rather than by request size.
        private static RlpLimit BlockReceiptsRlpLimit => RlpLimit.For<TxReceipt[]>(BlockBodyDecoder.TransactionsCountLimit.Limit, nameof(ReceiptsMessage.TxReceipts));
        private readonly ISpecProvider _specProvider;
        private readonly IRlpDecoder<TxReceipt> _decoder;
        private readonly DecodeRlpValue<TxReceipt[]> _decodeArrayFunc;

        public ReceiptsMessageSerializer(ISpecProvider specProvider) : this(specProvider, Rlp.GetDecoderOrThrow<TxReceipt>()) { }

        protected ReceiptsMessageSerializer(ISpecProvider specProvider, IRlpDecoder<TxReceipt> decoder)
        {
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _decoder = decoder;
            // The message does not say which blocks the receipts belong to, so a zero-length bloom (EIP-7668) is
            // accepted whenever the chain ever schedules the EIP; the receipts root rejects one before the fork.
            RlpBehaviors decodeBehaviors = specProvider.GetFinalSpec().IsEip7668Enabled ? RlpBehaviors.Eip7668Receipts : RlpBehaviors.None;
            _decodeArrayFunc = (ref RlpReader ctx) => ctx.DecodeNonNullArray((ref RlpReader nestedContext) => _decoder.Decode(ref nestedContext, decodeBehaviors), limit: BlockReceiptsRlpLimit);
        }

        public void Serialize(IByteBuffer byteBuffer, ReceiptsMessage message) => this.SerializeWithLengths(byteBuffer, message);

        public void Serialize(IByteBuffer byteBuffer, ReceiptsMessage message, ref LengthStack lengths)
        {
            ByteBufferRlpWriter writer = new(byteBuffer);
            writer.StartSequence(lengths.Pop());

            ulong lastBlockNumber = NoBlockSeenYet;
            RlpBehaviors behaviors = RlpBehaviors.None;

            foreach (TxReceipt[]? txReceipts in message.TxReceipts.AsSpan())
            {
                if (txReceipts is null)
                {
                    writer.Encode(Rlp.OfEmptyList);
                    continue;
                }

                writer.StartSequence(lengths.Pop());
                foreach (TxReceipt txReceipt in txReceipts)
                {
                    // Only fetch a new spec when the block number changes
                    if (txReceipt.BlockNumber != lastBlockNumber)
                    {
                        lastBlockNumber = txReceipt.BlockNumber;
                        IReceiptSpec receiptSpec = _specProvider.GetReceiptSpec(lastBlockNumber);
                        behaviors = receiptSpec.IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None;
                    }

                    _decoder.Encode(ref writer, txReceipt, ref lengths, behaviors);
                }
            }
        }

        public ReceiptsMessage Deserialize(IByteBuffer byteBuffer)
        {
            if (byteBuffer.ReadableBytes == 0)
            {
                return new ReceiptsMessage(null);
            }

            if (byteBuffer.GetByte(byteBuffer.ReaderIndex) == Rlp.OfEmptyList[0])
            {
                byteBuffer.ReadByte();
                return new ReceiptsMessage(null);
            }

            return byteBuffer.DeserializeRlp(Deserialize);
        }

        public ReceiptsMessage Deserialize(ref RlpReader ctx)
        {
            ArrayPoolList<TxReceipt[]> data = ctx.DecodeNonNullArrayPoolList(_decodeArrayFunc, defaultElement: [], limit: ReceiptsRlpLimit);
            return new ReceiptsMessage(data);
        }

        public int PopulateLength(ReceiptsMessage message, ref LengthStack lengths)
        {
            int contentLength = 0;

            ReadOnlySpan<TxReceipt[]?> txReceiptsSpan = message.TxReceipts.AsSpan();
            for (int i = txReceiptsSpan.Length - 1; i >= 0; i--)
            {
                TxReceipt[]? txReceipts = txReceiptsSpan[i];
                if (txReceipts is null)
                {
                    contentLength += Rlp.OfEmptyList.Length;
                    continue;
                }

                int innerLength = PopulateInnerLength(txReceipts, ref lengths);
                lengths.Push(innerLength);
                contentLength += Rlp.LengthOfSequence(innerLength);
            }

            lengths.Push(contentLength);
            return Rlp.LengthOfSequence(contentLength);
        }

        private int PopulateInnerLength(TxReceipt[] txReceipts, ref LengthStack lengths)
        {
            if (txReceipts.Length == 0)
                return 0;

            int contentLength = 0;

            ulong lastBlockNumber = NoBlockSeenYet;
            RlpBehaviors behaviors = RlpBehaviors.None;

            for (int i = txReceipts.Length - 1; i >= 0; i--)
            {
                TxReceipt receipt = txReceipts[i];

                // Only fetch a new spec when block number changes
                if (lastBlockNumber != receipt.BlockNumber)
                {
                    lastBlockNumber = receipt.BlockNumber;
                    IReceiptSpec receiptSpec = _specProvider.GetReceiptSpec(lastBlockNumber);
                    behaviors = receiptSpec.IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None;
                }

                contentLength += _decoder.PopulateLength(receipt, ref lengths, behaviors);
            }

            return contentLength;
        }
    }
}

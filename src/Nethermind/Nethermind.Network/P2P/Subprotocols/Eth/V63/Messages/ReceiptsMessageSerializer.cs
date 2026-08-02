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
        private static readonly RlpLimit BlockReceiptsRlpLimit = RlpLimit.For<TxReceipt[]>(NethermindSyncLimits.MaxHashesFetch, nameof(ReceiptsMessage.TxReceipts));
        private readonly ISpecProvider _specProvider;
        private readonly IRlpDecoder<TxReceipt> _decoder;
        private readonly DecodeRlpValue<TxReceipt[]> _decodeArrayFunc;

        public ReceiptsMessageSerializer(ISpecProvider specProvider) : this(specProvider, Rlp.GetDecoderOrThrow<TxReceipt>()) { }

        protected ReceiptsMessageSerializer(ISpecProvider specProvider, IRlpDecoder<TxReceipt> decoder)
        {
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _decoder = decoder;
            _decodeArrayFunc = (ref RlpReader ctx) => ctx.DecodeArray((ref RlpReader nestedContext) => _decoder.Decode(ref nestedContext), limit: BlockReceiptsRlpLimit);
        }

        public void Serialize(IByteBuffer byteBuffer, ReceiptsMessage message)
        {
            int totalLength = GetLength(message, out int contentLength);

            byteBuffer.EnsureWritable(totalLength);
            ByteBufferRlpWriter writer = new(byteBuffer);
            writer.StartSequence(contentLength);

            ulong lastBlockNumber = NoBlockSeenYet;
            RlpBehaviors behaviors = RlpBehaviors.None;

            foreach (TxReceipt[]? txReceipts in message.TxReceipts.AsSpan())
            {
                if (txReceipts is null)
                {
                    writer.Encode(Rlp.OfEmptyList);
                    continue;
                }

                int innerLength = GetInnerLength(txReceipts);
                writer.StartSequence(innerLength);
                foreach (TxReceipt txReceipt in txReceipts)
                {
                    // Only fetch a new spec when the block number changes
                    if (txReceipt.BlockNumber != lastBlockNumber)
                    {
                        lastBlockNumber = txReceipt.BlockNumber;
                        IReceiptSpec receiptSpec = _specProvider.GetReceiptSpec(lastBlockNumber);
                        behaviors = receiptSpec.IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None;
                    }

                    _decoder.Encode(ref writer, txReceipt, behaviors);
                }
            }
        }

        public ReceiptsMessage Deserialize(IByteBuffer byteBuffer)
        {
            if (byteBuffer.ReadableBytes == 0)
            {
                return ReceiptsMessage.Empty;
            }

            if (byteBuffer.GetByte(byteBuffer.ReaderIndex) == Rlp.OfEmptyList[0])
            {
                byteBuffer.ReadByte();
                return ReceiptsMessage.Empty;
            }

            return byteBuffer.DeserializeRlp(Deserialize);
        }

        public ReceiptsMessage Deserialize(ref RlpReader ctx)
        {
            ArrayPoolList<TxReceipt[]> data = ctx.DecodeArrayPoolList(_decodeArrayFunc, defaultElement: [], limit: ReceiptsRlpLimit);
            return new ReceiptsMessage(data);
        }

        public int GetLength(ReceiptsMessage message, out int contentLength)
        {
            contentLength = 0;

            ReadOnlySpan<TxReceipt[]?> txReceiptsSpan = message.TxReceipts.AsSpan();
            for (int i = 0; i < txReceiptsSpan.Length; i++)
            {
                TxReceipt[]? txReceipts = txReceiptsSpan[i];
                contentLength += txReceipts is null
                    ? Rlp.OfEmptyList.Length
                    : Rlp.LengthOfSequence(GetInnerLength(txReceipts));
            }

            return Rlp.LengthOfSequence(contentLength);
        }

        private int GetInnerLength(TxReceipt[] txReceipts)
        {
            if (txReceipts.Length == 0)
                return 0;

            int contentLength = 0;

            ulong lastBlockNumber = NoBlockSeenYet;
            RlpBehaviors behaviors = RlpBehaviors.None;

            for (int i = 0; i < txReceipts.Length; i++)
            {
                TxReceipt receipt = txReceipts[i];

                // Only fetch a new spec when block number changes
                if (lastBlockNumber != receipt.BlockNumber)
                {
                    lastBlockNumber = receipt.BlockNumber;
                    IReceiptSpec receiptSpec = _specProvider.GetReceiptSpec(lastBlockNumber);
                    behaviors = receiptSpec.IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None;
                }

                contentLength += _decoder.GetLength(receipt, behaviors);
            }

            return contentLength;
        }
    }
}

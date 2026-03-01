// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages
{
    public class ReceiptsMessageSerializer : IZeroInnerMessageSerializer<ReceiptsMessage>
    {
        private readonly ISpecProvider _specProvider;
        private readonly IRlpStreamEncoder<TxReceipt> _encoder;
        private readonly IRlpValueDecoder<TxReceipt> _decoder;
        private readonly DecodeRlpValue<TxReceipt[]> _decodeArrayFunc;

        public ReceiptsMessageSerializer(ISpecProvider specProvider) : this(specProvider, (RlpValueDecoder<TxReceipt>)Rlp.GetValueDecoder<TxReceipt>()!) { }

        protected ReceiptsMessageSerializer(ISpecProvider specProvider, RlpValueDecoder<TxReceipt> decoder)
        {
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _encoder = decoder;
            _decoder = decoder;
            _decodeArrayFunc = (ref Rlp.ValueDecoderContext ctx) => ctx.DecodeArray((ref Rlp.ValueDecoderContext nestedContext) => _decoder.Decode(ref nestedContext)) ?? [];
        }

        public void Serialize(IByteBuffer byteBuffer, ReceiptsMessage message)
        {
            int totalLength = GetLength(message, out int contentLength);

            byteBuffer.EnsureWritable(totalLength);
            NettyRlpStream stream = new(byteBuffer);
            stream.StartSequence(contentLength);

            // Track the last ‐ seen block number & its RLP behavior
            long lastBlockNumber = -1;
            RlpBehaviors behaviors = RlpBehaviors.None;

            foreach (TxReceipt?[]? txReceipts in message.TxReceipts)
            {
                if (txReceipts is null)
                {
                    stream.Encode(Rlp.OfEmptyList);
                    continue;
                }

                int innerLength = GetInnerLength(txReceipts);
                stream.StartSequence(innerLength);
                foreach (TxReceipt? txReceipt in txReceipts)
                {
                    if (txReceipt is null)
                    {
                        stream.Encode(Rlp.OfEmptyList);
                        continue;
                    }

                    // Only fetch a new spec when the block number changes
                    if (txReceipt.BlockNumber != lastBlockNumber)
                    {
                        lastBlockNumber = txReceipt.BlockNumber;
                        behaviors = _specProvider.GetReceiptSpec(lastBlockNumber).IsEip658Enabled
                                    ? RlpBehaviors.Eip658Receipts
                                    : RlpBehaviors.None;
                    }

                    _encoder.Encode(stream, txReceipt, behaviors);
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

            Rlp.ValueDecoderContext ctx = byteBuffer.AsRlpContext();
            ReceiptsMessage message = Deserialize(ref ctx);
            byteBuffer.SetReaderIndex(byteBuffer.ReaderIndex + ctx.Position);
            return message;
        }

        public ReceiptsMessage Deserialize(ref Rlp.ValueDecoderContext ctx)
        {
            ArrayPoolList<TxReceipt[]> data = ctx.DecodeArrayPoolList(_decodeArrayFunc);
            ReceiptsMessage message = new(data);

            return message;
        }

        public int GetLength(ReceiptsMessage message, out int contentLength)
        {
            contentLength = 0;

            for (int i = 0; i < message.TxReceipts.Count; i++)
            {
                TxReceipt?[]? txReceipts = message.TxReceipts[i];
                if (txReceipts is null)
                {
                    contentLength += Rlp.OfEmptyList.Length;
                }
                else
                {
                    contentLength += Rlp.LengthOfSequence(GetInnerLength(txReceipts));
                }
            }

            return Rlp.LengthOfSequence(contentLength);
        }

        private int GetInnerLength(TxReceipt?[]? txReceipts)
        {
            if (txReceipts == null || txReceipts.Length == 0)
                return 0;

            int contentLength = 0;

            // Track the last‐seen block number and its spec
            long lastBlockNumber = -1;
            RlpBehaviors behaviors = RlpBehaviors.None;

            for (int i = 0; i < txReceipts.Length; i++)
            {
                TxReceipt? receipt = txReceipts[i];

                if (receipt is null)
                {
                    contentLength += Rlp.OfEmptyList.Length;
                    continue;
                }

                // Only fetch a new spec when block number changes
                if (lastBlockNumber != receipt.BlockNumber)
                {
                    lastBlockNumber = receipt.BlockNumber;
                    behaviors = _specProvider.GetSpec((ForkActivation)receipt.BlockNumber).IsEip658Enabled
                                ? RlpBehaviors.Eip658Receipts
                                : RlpBehaviors.None;
                }

                contentLength += _decoder.GetLength(receipt, behaviors);
            }

            return contentLength;
        }
    }
}

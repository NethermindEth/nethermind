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
        private static readonly IRlpStreamDecoder<TxReceipt> _decoder = Rlp.GetStreamDecoder<TxReceipt>();

        public ReceiptsMessageSerializer(ISpecProvider specProvider)
        {
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        }

        public void Serialize(IByteBuffer byteBuffer, ReceiptsMessage message)
        {
            int totalLength = GetLength(message, out int contentLength);

            byteBuffer.EnsureWritable(totalLength);
            NettyRlpStream stream = new(byteBuffer);
            stream.StartSequence(contentLength);

            foreach (TxReceipt?[]? txReceipts in message.TxReceipts)
            {
                if (txReceipts is null)
                {
                    stream.Encode(Rlp.OfEmptySequence);
                    continue;
                }

                int innerLength = GetInnerLength(txReceipts);
                stream.StartSequence(innerLength);
                foreach (TxReceipt? txReceipt in txReceipts)
                {
                    if (txReceipt is null)
                    {
                        stream.Encode(Rlp.OfEmptySequence);
                        continue;
                    }

                    _decoder.Encode(stream, txReceipt, GetEncodingBehavior(txReceipt));
                }
            }
        }

        public ReceiptsMessage Deserialize(IByteBuffer byteBuffer)
        {
            if (byteBuffer.ReadableBytes == 0)
            {
                return ReceiptsMessage.Empty;
            }

            if (byteBuffer.GetByte(byteBuffer.ReaderIndex) == Rlp.OfEmptySequence[0])
            {
                byteBuffer.ReadByte();
                return ReceiptsMessage.Empty;
            }

            RlpStream rlpStream = new NettyRlpStream(byteBuffer);
            return Deserialize(rlpStream);
        }

        public ReceiptsMessage Deserialize(RlpStream rlpStream)
        {
            ArrayPoolList<TxReceipt[]> data = rlpStream.DecodeArrayPoolList(itemContext =>
                itemContext.DecodeArray(nestedContext => _decoder.Decode(nestedContext, GetDecodingBehavior())));
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
                    contentLength += Rlp.OfEmptySequence.Length;
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
            int contentLength = 0;
            for (int j = 0; j < txReceipts.Length; j++)
            {
                TxReceipt? txReceipt = txReceipts[j];
                if (txReceipt is null)
                {
                    contentLength += Rlp.OfEmptySequence.Length;
                }
                else
                {
                    contentLength += _decoder.GetLength(txReceipt, GetEncodingBehavior(txReceipt));
                }
            }

            return contentLength;
        }

        protected virtual RlpBehaviors GetEncodingBehavior(TxReceipt txReceipt)
        {
            return _specProvider.GetReceiptSpec(txReceipt.BlockNumber).IsEip658Enabled
                ? RlpBehaviors.Eip658Receipts
                : RlpBehaviors.None;
        }

        protected virtual RlpBehaviors GetDecodingBehavior() => RlpBehaviors.None;
    }
}

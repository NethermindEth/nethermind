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
        private readonly RlpBehaviors _additionalBehaviors;
        private static readonly IRlpStreamDecoder<TxReceipt> _decoder = Rlp.GetStreamDecoder<TxReceipt>();

        public ReceiptsMessageSerializer(ISpecProvider specProvider, RlpBehaviors additionalBehaviors = RlpBehaviors.None)
        {
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _additionalBehaviors = additionalBehaviors;
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

                    RlpBehaviors behaviors = (_specProvider.GetReceiptSpec(txReceipt.BlockNumber).IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None) | _additionalBehaviors;
                    _decoder.Encode(stream, txReceipt, behaviors);
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
            ArrayPoolList<TxReceipt[]> data = rlpStream.DecodeArrayPoolList(static itemContext =>
                itemContext.DecodeArray(static nestedContext => _decoder.Decode(nestedContext)) ?? [], true);
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
                    RlpBehaviors behaviors = (_specProvider.GetSpec((ForkActivation)txReceipt.BlockNumber).IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None) | _additionalBehaviors;
                    contentLength += _decoder.GetLength(txReceipt, behaviors);
                }
            }

            return contentLength;
        }
    }
}

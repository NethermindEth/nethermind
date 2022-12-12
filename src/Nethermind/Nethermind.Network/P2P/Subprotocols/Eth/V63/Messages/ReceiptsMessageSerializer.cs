// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages
{
    // 3% (2GB) allocation of Goerli 3m fast sync that can be improved by implementing ZeroMessageSerializer here
    public class ReceiptsMessageSerializer : IZeroInnerMessageSerializer<ReceiptsMessage>
    {
        private readonly ISpecProvider _specProvider;
        private readonly ReceiptMessageDecoder _decoder = new();

        public ReceiptsMessageSerializer(ISpecProvider specProvider)
        {
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        }

        public void Serialize(IByteBuffer byteBuffer, ReceiptsMessage message)
        {
            Rlp rlp = Rlp.Encode(message.TxReceipts.Select(
                b => b is null
                    ? Rlp.OfEmptySequence
                    : Rlp.Encode(
                        b.Select(
                            n => n is null
                                ? Rlp.OfEmptySequence
                                // for TxReceipt there is no timestamp, as such, we are keeping the old implementation. wonder how we can metigate this later if future EIPs affecting this are added. 
                                : _decoder.Encode(n, _specProvider.GetSpec((ForkActivation)n.BlockNumber).IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None)).ToArray())).ToArray());

            RlpStream rlpStream = new NettyRlpStream(byteBuffer);
            rlpStream.Encode(rlp);
        }

        public ReceiptsMessage Deserialize(IByteBuffer byteBuffer)
        {
            if (byteBuffer.Array.Length == 0 || byteBuffer.Array.First() == Rlp.OfEmptySequence[0]) return new ReceiptsMessage(null);

            RlpStream rlpStream = new NettyRlpStream(byteBuffer);
            return Deserialize(rlpStream);
        }

        public ReceiptsMessage Deserialize(RlpStream rlpStream)
        {
            TxReceipt[][] data = rlpStream.DecodeArray(itemContext =>
                itemContext.DecodeArray(nestedContext => _decoder.Decode(nestedContext)) ?? new TxReceipt[0], true);
            ReceiptsMessage message = new(data);

            return message;
        }

        public int GetLength(ReceiptsMessage message, out int contentLength)
        {
            contentLength = 0;

            for (int i = 0; i < message.TxReceipts.Length; i++)
            {
                TxReceipt?[]? txReceipts = message.TxReceipts[i];
                if (txReceipts is null)
                {
                    contentLength += Rlp.OfEmptySequence.Length;
                }
                else
                {
                    for (int j = 0; j < txReceipts.Length; j++)
                    {
                        TxReceipt? txReceipt = txReceipts[j];
                        if (txReceipt is null)
                        {
                            contentLength += Rlp.OfEmptySequence.Length;
                        }
                        else
                        {
                            // same as above comment. TxReceipt has no timestamp
                            contentLength += Rlp.LengthOfSequence(_decoder.GetLength(txReceipt, _specProvider.GetSpec((ForkActivation)txReceipt.BlockNumber).IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None));
                        }
                    }
                }

            }

            return Rlp.LengthOfSequence(contentLength);
        }
    }
}

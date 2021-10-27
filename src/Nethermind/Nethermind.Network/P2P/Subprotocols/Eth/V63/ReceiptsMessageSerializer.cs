//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Linq;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V63
{
    // 3% (2GB) allocation of Goerli 3m fast sync that can be improved by implementing ZeroMessageSerializer here
    public class ReceiptsMessageSerializer : IZeroInnerMessageSerializer<ReceiptsMessage>
    {
        private readonly ISpecProvider _specProvider;
        private readonly ReceiptMessageDecoder _decoder = new ReceiptMessageDecoder();

        public ReceiptsMessageSerializer(ISpecProvider specProvider)
        {
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        }
        
        public void Serialize(IByteBuffer byteBuffer, ReceiptsMessage message)
        {
            Rlp rlp = Rlp.Encode(message.TxReceipts.Select(
                b => b == null
                    ? Rlp.OfEmptySequence
                    : Rlp.Encode(
                        b.Select(
                            n => n == null
                                ? Rlp.OfEmptySequence
                                : _decoder.Encode(n, _specProvider.GetSpec(n.BlockNumber).IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None)).ToArray())).ToArray());
            
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
            ReceiptsMessage message = new ReceiptsMessage(data);

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
                            contentLength += Rlp.LengthOfSequence(_decoder.GetLength(txReceipt, _specProvider.GetSpec(txReceipt.BlockNumber).IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None));
                        }
                    }
                }

            }
            
            return Rlp.LengthOfSequence(contentLength);
        }
    }
}

// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Les.Messages
{
    public class ReceiptsMessageSerializer : IZeroMessageSerializer<ReceiptsMessage>
    {
        private readonly ISpecProvider _specProvider;

        public ReceiptsMessageSerializer(ISpecProvider specProvider)
        {
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        }

        public void Serialize(IByteBuffer byteBuffer, ReceiptsMessage message)
        {
            Eth.V63.Messages.ReceiptsMessageSerializer ethSerializer = new(_specProvider);
            Rlp ethMessage = new(ethSerializer.Serialize(message.EthMessage));
            int contentLength = Rlp.LengthOf(message.RequestId) + Rlp.LengthOf(message.BufferValue) + ethMessage.Length;

            int totalLength = Rlp.LengthOfSequence(contentLength);

            RlpStream rlpStream = new NettyRlpStream(byteBuffer);
            byteBuffer.EnsureWritable(totalLength);

            rlpStream.StartSequence(contentLength);
            rlpStream.Encode(message.RequestId);
            rlpStream.Encode(message.BufferValue);
            rlpStream.Encode(ethMessage);
        }

        public ReceiptsMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new(byteBuffer);
            return Deserialize(rlpStream);
        }

        public ReceiptsMessage Deserialize(RlpStream rlpStream)
        {
            ReceiptsMessage receiptsMessage = new();
            Eth.V63.Messages.ReceiptsMessageSerializer ethSerializer = new(_specProvider);

            rlpStream.ReadSequenceLength();
            receiptsMessage.RequestId = rlpStream.DecodeLong();
            receiptsMessage.BufferValue = rlpStream.DecodeInt();
            receiptsMessage.EthMessage = ethSerializer.Deserialize(rlpStream);
            return receiptsMessage;
        }
    }
}

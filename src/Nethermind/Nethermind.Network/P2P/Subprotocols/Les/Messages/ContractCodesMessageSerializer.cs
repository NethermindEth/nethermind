// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Les.Messages
{
    public class ContractCodesMessageSerializer : IZeroMessageSerializer<ContractCodesMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, ContractCodesMessage message)
        {
            int innerLength = 0;
            for (int i = 0; i < message.Codes.Length; i++)
            {
                innerLength += Rlp.LengthOf(message.Codes[i]);
            }
            int contentLength =
                Rlp.LengthOf(message.RequestId) +
                Rlp.LengthOf(message.BufferValue) +
                Rlp.LengthOfSequence(innerLength);

            int totalLength = Rlp.LengthOfSequence(contentLength);

            RlpStream rlpStream = new NettyRlpStream(byteBuffer);
            byteBuffer.EnsureWritable(totalLength);

            rlpStream.StartSequence(contentLength);
            rlpStream.Encode(message.RequestId);
            rlpStream.Encode(message.BufferValue);
            rlpStream.StartSequence(innerLength);
            for (int i = 0; i < message.Codes.Length; i++)
            {
                rlpStream.Encode(message.Codes[i]);
            }
        }

        public ContractCodesMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new(byteBuffer);
            return Deserialize(rlpStream);
        }

        public static ContractCodesMessage Deserialize(RlpStream rlpStream)
        {
            ContractCodesMessage contractCodesMessage = new();
            rlpStream.ReadSequenceLength();
            contractCodesMessage.RequestId = rlpStream.DecodeLong();
            contractCodesMessage.BufferValue = rlpStream.DecodeInt();
            contractCodesMessage.Codes = rlpStream.DecodeArray(stream => stream.DecodeByteArray());
            return contractCodesMessage;
        }
    }
}

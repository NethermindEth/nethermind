// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Les.Messages
{
    public class GetHelperTrieProofsMessageSerializer : IZeroMessageSerializer<GetHelperTrieProofsMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, GetHelperTrieProofsMessage message)
        {
            int innerLength = 0;
            foreach (var request in message.Requests)
            {
                innerLength += Rlp.LengthOfSequence(GetRequestLength(request));
            }
            int contentLength = Rlp.LengthOf(message.RequestId) +
                Rlp.LengthOfSequence(innerLength);

            int totalLength = Rlp.LengthOfSequence(contentLength);

            RlpStream rlpStream = new NettyRlpStream(byteBuffer);
            byteBuffer.EnsureWritable(totalLength);

            rlpStream.StartSequence(contentLength);
            rlpStream.Encode(message.RequestId);
            rlpStream.StartSequence(innerLength);
            foreach (var request in message.Requests)
            {
                rlpStream.StartSequence(GetRequestLength(request));
                rlpStream.Encode((int)request.SubType);
                rlpStream.Encode(request.SectionIndex);
                rlpStream.Encode(request.Key);
                rlpStream.Encode(request.FromLevel);
                rlpStream.Encode(request.AuxiliaryData);
            }
        }

        private int GetRequestLength(HelperTrieRequest request)
        {
            return
                Rlp.LengthOf((int)request.SubType) +
                Rlp.LengthOf(request.SectionIndex) +
                Rlp.LengthOf(request.Key) +
                Rlp.LengthOf(request.FromLevel) +
                Rlp.LengthOf(request.AuxiliaryData);
        }

        public GetHelperTrieProofsMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new(byteBuffer);
            return Deserialize(rlpStream);
        }

        public static GetHelperTrieProofsMessage Deserialize(RlpStream rlpStream)
        {
            GetHelperTrieProofsMessage message = new();
            rlpStream.ReadSequenceLength();
            message.RequestId = rlpStream.DecodeLong();
            message.Requests = rlpStream.DecodeArray(stream =>
            {
                HelperTrieRequest request = new();
                stream.ReadSequenceLength();
                request.SubType = (HelperTrieType)stream.DecodeInt();
                request.SectionIndex = stream.DecodeLong();
                request.Key = stream.DecodeByteArray();
                request.FromLevel = stream.DecodeLong();
                request.AuxiliaryData = stream.DecodeInt();
                return request;
            });
            return message;
        }
    }
}

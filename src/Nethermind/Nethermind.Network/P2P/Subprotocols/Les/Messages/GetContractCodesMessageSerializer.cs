// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Les.Messages
{
    public class GetContractCodesMessageSerializer : IZeroMessageSerializer<GetContractCodesMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, GetContractCodesMessage message)
        {
            // note: If there are any changes to how a hash is encoded, this will break (compression?)
            // calling LengthOf for each hash would be more resistant to future changes, if we think there will be any
            int requestLength = Rlp.LengthOf(Keccak.OfAnEmptyString) * 2;
            int allRequestsLength = Rlp.LengthOfSequence(requestLength) * message.Requests.Length;
            int contentLength =
                Rlp.LengthOf(message.RequestId) +
                Rlp.LengthOfSequence(allRequestsLength);

            int totalLength = Rlp.LengthOfSequence(contentLength);

            RlpStream rlpStream = new NettyRlpStream(byteBuffer);
            byteBuffer.EnsureWritable(totalLength);

            rlpStream.StartSequence(contentLength);
            rlpStream.Encode(message.RequestId);

            rlpStream.StartSequence(allRequestsLength);
            foreach (CodeRequest request in message.Requests)
            {
                rlpStream.StartSequence(requestLength);
                rlpStream.Encode(request.BlockHash);
                rlpStream.Encode(request.AccountKey);
            }
        }

        public GetContractCodesMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new(byteBuffer);
            return Deserialize(rlpStream);
        }

        public static GetContractCodesMessage Deserialize(RlpStream rlpStream)
        {
            GetContractCodesMessage getContractCodesMessage = new();
            rlpStream.ReadSequenceLength();
            getContractCodesMessage.RequestId = rlpStream.DecodeLong();
            getContractCodesMessage.Requests = rlpStream.DecodeArray(stream =>
            {
                CodeRequest request = new();
                stream.ReadSequenceLength();
                request.BlockHash = stream.DecodeKeccak();
                request.AccountKey = stream.DecodeKeccak();
                return request;
            });

            return getContractCodesMessage;
        }
    }
}

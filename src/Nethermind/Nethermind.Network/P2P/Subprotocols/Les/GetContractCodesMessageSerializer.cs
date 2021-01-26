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

using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Les
{
    public class GetContractCodesMessageSerializer: IZeroMessageSerializer<GetContractCodesMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, GetContractCodesMessage message)
        {
            // note: If there are any changes to how a hash is encoded, this will break (compression?)
            // calling LengthOf for each hash would be more resistant to future changes, if we think there will be any
            int requestLength = Rlp.LengthOf(Keccak.OfAnEmptyString) * 2;
            int allRequestsLength = Rlp.GetSequenceRlpLength(requestLength) * message.Requests.Length;
            int contentLength =
                Rlp.LengthOf(message.RequestId) +
                Rlp.GetSequenceRlpLength(allRequestsLength);

            int totalLength = Rlp.GetSequenceRlpLength(contentLength);

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
            NettyRlpStream rlpStream = new NettyRlpStream(byteBuffer);
            return Deserialize(rlpStream);
        }

        public static GetContractCodesMessage Deserialize(RlpStream rlpStream)
        {
            GetContractCodesMessage getContractCodesMessage = new GetContractCodesMessage();
            rlpStream.ReadSequenceLength();
            getContractCodesMessage.RequestId = rlpStream.DecodeLong();
            getContractCodesMessage.Requests = rlpStream.DecodeArray(stream =>
            {
                CodeRequest request = new CodeRequest();
                stream.ReadSequenceLength();
                request.BlockHash = stream.DecodeKeccak();
                request.AccountKey = stream.DecodeKeccak();
                return request;
            });

            return getContractCodesMessage;
        }
    }
}

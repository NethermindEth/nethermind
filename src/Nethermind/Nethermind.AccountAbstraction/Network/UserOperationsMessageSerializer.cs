
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
// 

using DotNetty.Buffers;
using Nethermind.AccountAbstraction.Data;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Serialization.Rlp;

namespace Nethermind.AccountAbstraction.Network
{
    public class UserOperationsMessageSerializer : IZeroInnerMessageSerializer<UserOperationsMessage>
    {
        private UserOperationDecoder _decoder = new();
        
        public void Serialize(IByteBuffer byteBuffer, UserOperationsMessage message)
        {
            int length = GetLength(message, out int contentLength);
            byteBuffer.EnsureWritable(length, true);
            NettyRlpStream nettyRlpStream = new(byteBuffer);
            
            nettyRlpStream.StartSequence(contentLength);
            for (int i = 0; i < message.UserOperations.Count; i++)
            {
                nettyRlpStream.Encode(message.UserOperations[i]);
            }
        }

        public UserOperationsMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new(byteBuffer);
            UserOperation[] uOps = DeserializeUOps(rlpStream);
            return new UserOperationsMessage(uOps);
        }

        public int GetLength(UserOperationsMessage message, out int contentLength)
        {
            contentLength = 0;
            for (int i = 0; i < message.UserOperations.Count; i++)
            {
                contentLength += _decoder.GetLength(message.UserOperations[i], RlpBehaviors.None);
            }

            return Rlp.LengthOfSequence(contentLength);
        }
        
        private UserOperation[] DeserializeUOps(NettyRlpStream rlpStream)
        {
            return Rlp.DecodeArray<UserOperation>(rlpStream);
        }
    }
}

// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.AccountAbstraction.Data;
using Nethermind.Network;
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
            for (int i = 0; i < message.UserOperationsWithEntryPoint.Count; i++)
            {
                nettyRlpStream.Encode(message.UserOperationsWithEntryPoint[i]);
            }
        }

        public UserOperationsMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new(byteBuffer);
            UserOperationWithEntryPoint[] uOps = DeserializeUOps(rlpStream);
            return new UserOperationsMessage(uOps);
        }

        public int GetLength(UserOperationsMessage message, out int contentLength)
        {
            contentLength = 0;
            for (int i = 0; i < message.UserOperationsWithEntryPoint.Count; i++)
            {
                contentLength += _decoder.GetLength(message.UserOperationsWithEntryPoint[i], RlpBehaviors.None);
            }

            return Rlp.LengthOfSequence(contentLength);
        }

        private UserOperationWithEntryPoint[] DeserializeUOps(NettyRlpStream rlpStream)
        {
            return Rlp.DecodeArray<UserOperationWithEntryPoint>(rlpStream);
        }
    }
}
